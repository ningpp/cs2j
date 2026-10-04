using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.AnonymousTypeLift;

/// <summary>
/// Assigns sequential slot numbers to type parameters on first appearance. Slot
/// numbers are dense (0..n-1) in structural first-appearance order, which makes them
/// canonical: structurally identical open-generic shapes number their parameters
/// identically regardless of which method/class declares them.
/// (结构照搬 E06 TupleShapeRegistry.SlotAssigner。)
/// </summary>
internal sealed class SlotAssigner
{
    private readonly Dictionary<ITypeParameterSymbol, int> _slots =
        new(SymbolEqualityComparer.Default);

    public int Count => _slots.Count;

    public int GetOrAdd(ITypeParameterSymbol parameter)
    {
        if (_slots.TryGetValue(parameter, out var slot))
        {
            return slot;
        }

        slot = _slots.Count;
        _slots[parameter] = slot;
        return slot;
    }

    /// <summary>约束收集用：只查不登记。</summary>
    public bool TryGet(ITypeParameterSymbol parameter, out int slot)
        => _slots.TryGetValue(parameter, out slot);
}

/// <summary>One property of an anonymous-object shape: closed/open type, type-parameter slot, or nested anonymous shape.</summary>
internal sealed record AnonymousPropertySpec
{
    /// <summary>Property name as declared (case sensitive, projection shorthand keeps the inferred name).</summary>
    public required string Name { get; init; }

    /// <summary>Canonical key fragment used in the shape key: <c>PropertyName:typeKey</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Mangled identifier fragment used when composing the class name.</summary>
    public required string MangledName { get; init; }

    /// <summary>
    /// Fully-qualified display text for the generated property, with <c>{slot}</c>
    /// placeholders (slot numbers are canonical for the enclosing shape) replaced by
    /// the class's own generic parameters at generation time. Dynamic properties
    /// render as the literal text <c>dynamic</c>. Anonymous types appearing anywhere
    /// inside (property, generic argument, array element) are already replaced by
    /// their generated class instantiation, so the template is anonymous-free.
    /// </summary>
    public required string DisplayTemplate { get; init; }

    /// <summary>Set when the property is itself an anonymous type; its class instantiation.</summary>
    public AnonymousShape? NestedShape { get; init; }

    /// <summary>For nested anonymous properties: nested generic parameter i binds to this outer slot.</summary>
    public IReadOnlyList<int> NestedArgSlots { get; init; } = Array.Empty<int>();

    /// <summary>True when the property type itself is <c>dynamic</c> (distinct from object, D2/D9).</summary>
    public bool IsDynamic { get; init; }

    public bool IsAnonymous => NestedShape != null;

    /// <summary>
    /// False when the property type is not publicly accessible outside its assembly.
    /// The generated classes are always internal in this feature; the value is kept
    /// for diagnostic/information purposes only (mirrors the E06 fact function).
    /// </summary>
    public bool IsPubliclyAccessible { get; init; } = true;
}

/// <summary>Constraints copied from the source type parameter backing one shape slot (D12).</summary>
internal sealed class AnonymousTypeParameterConstraints
{
    public int Slot { get; init; }
    public bool HasClass { get; init; }
    public bool HasStruct { get; init; }
    public bool HasUnmanaged { get; init; }
    public bool HasNotNull { get; init; }
    public bool HasNew { get; init; }

    /// <summary>Base-class/interface constraint templates with <c>{slot}</c> placeholders.</summary>
    public IReadOnlyList<string> ConstraintTypeTemplates { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A registered anonymous shape: the structural identity of a C# anonymous type plus
/// the name of the internal sealed class generated for it. Shapes are deduplicated by
/// canonical key (property sequence + constraint signature) so identical shapes map
/// to one class across methods/files/compilation units.
/// </summary>
internal sealed class AnonymousShape
{
    /// <summary>Canonical structural key, e.g. <c>(X:global::System.Int32;Y:global::System.String)</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Generated class simple name (open shapes include T0/T1 fragments).</summary>
    public required string Name { get; init; }

    public IReadOnlyList<AnonymousPropertySpec> Properties { get; init; } = Array.Empty<AnonymousPropertySpec>();

    /// <summary>Number of generic parameters on the generated class.</summary>
    public int GenericArity { get; init; }

    /// <summary>Per-slot constraints copied from the source type parameters, ordered by slot.</summary>
    public IReadOnlyList<AnonymousTypeParameterConstraints> Constraints { get; init; }
        = Array.Empty<AnonymousTypeParameterConstraints>();

    /// <summary>Always-internal generation; the flag is informational only.</summary>
    public bool IsPubliclyAccessible { get; init; } = true;

    /// <summary>True when at least one property is typed <c>dynamic</c> (D9).</summary>
    public bool HasDynamic { get; init; }
}

/// <summary>
/// Builds, names, and deduplicates anonymous shapes across the whole compilation.
/// The registry is shared between the analysis pass (registers shapes from every
/// tree) and the rewrite pass (looks shapes up when emitting class names), keeping
/// class naming consistent across files.
///
/// Slot numbering is per-shape (see <see cref="SlotAssigner"/>): a shape's key uses
/// its own dense numbering, while nested anonymous instantiations embedded in an
/// outer shape's templates reference the outer shape's slots positionally.
/// </summary>
internal sealed partial class AnonymousShapeRegistry
{
    private readonly Dictionary<string, AnonymousShape> _shapesByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _takenNames = new(StringComparer.Ordinal);
    private readonly List<AnonymousShape> _ordered = new();
    private readonly string _namePrefix;

    public AnonymousShapeRegistry(string namePrefix = "Anon")
    {
        _namePrefix = namePrefix;
    }

    /// <summary>Shapes in deterministic registration order (nested shapes first).</summary>
    public IReadOnlyList<AnonymousShape> Shapes => _ordered;

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex SlotPlaceholderRegex();

    /// <summary>Distinct slot ids referenced by a display template, in order of appearance.</summary>
    internal static IReadOnlyList<int> SlotsInTemplate(string template)
    {
        var result = new List<int>();
        foreach (Match match in SlotPlaceholderRegex().Matches(template))
        {
            var slot = int.Parse(match.Groups[1].Value);
            if (!result.Contains(slot))
            {
                result.Add(slot);
            }
        }

        return result;
    }

    public AnonymousShape? FindByKey(string key) =>
        _shapesByKey.TryGetValue(key, out var shape) ? shape : null;

    /// <summary>
    /// Get or create the shape for an anonymous type symbol. The caller guarantees
    /// <paramref name="anonymousType"/> satisfies <c>IsAnonymousType</c>.
    /// Properties are taken in declaration order (parameterless
    /// <see cref="IPropertySymbol"/> members only); nested anonymous properties are
    /// registered recursively before the outer key is looked up, so nested shapes
    /// exist even when the outer shape is a cache hit.
    /// </summary>
    public AnonymousShape GetOrCreate(
        INamedTypeSymbol anonymousType,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        var properties = anonymousType.GetMembers()
            .OfType<IPropertySymbol>()
            .Where(p => p.Parameters.Length == 0)
            .ToArray();

        var slots = new SlotAssigner();
        var specs = new List<AnonymousPropertySpec>(properties.Length);
        var nameFragments = new List<string>(properties.Length);
        var hasDynamic = false;
        var publiclyAccessible = true;

        foreach (var property in properties)
        {
            var spec = BuildPropertySpec(property, slots, diagnostics);
            specs.Add(spec);
            nameFragments.Add("_" + SanitizeIdentifier(property.Name) + "_" + spec.MangledName);
            hasDynamic |= spec.IsDynamic;
            publiclyAccessible &= spec.IsPubliclyAccessible;
        }

        // Roslyn 把泛型方法/泛型类型里的匿名类型表示为「自身零形参」的符号，属性
        // 类型直接引用外层作用域的类型参数（带着源码约束）。因此形状的类型形参集
        // = 对属性类型做结构遍历收集到的去重类型参数（即槽位序），约束也从它们读。
        var shapeParameters = new List<ITypeParameterSymbol>();
        foreach (var property in properties)
        {
            CollectParameterOrder(property.Type, shapeParameters);
        }

        var constraints = BuildConstraints(shapeParameters, slots, diagnostics);

        var key = BuildKey(specs, constraints);
        if (_shapesByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var shape = new AnonymousShape
        {
            Key = key,
            Name = MakeUniqueName(nameFragments),
            Properties = specs,
            GenericArity = slots.Count,
            Constraints = constraints,
            IsPubliclyAccessible = publiclyAccessible,
            HasDynamic = hasDynamic,
        };
        _shapesByKey.Add(key, shape);
        _ordered.Add(shape);
        return shape;
    }

    private AnonymousPropertySpec BuildPropertySpec(
        IPropertySymbol property,
        SlotAssigner slots,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        var propertyType = property.Type;

        if (propertyType is INamedTypeSymbol { IsAnonymousType: true } nestedAnonymous)
        {
            var nestedShape = GetOrCreate(nestedAnonymous, diagnostics);
            var argSlots = BindParamsToSlots(nestedAnonymous, slots);
            return new AnonymousPropertySpec
            {
                Name = property.Name,
                Key = property.Name + ":" + "ANON" + nestedShape.Key
                    + "[" + string.Join(",", argSlots) + "]",
                MangledName = nestedShape.Name,
                DisplayTemplate = InstantiateShapeDisplay(nestedShape, argSlots),
                NestedShape = nestedShape,
                NestedArgSlots = argSlots,
                IsDynamic = false,
                IsPubliclyAccessible = nestedShape.IsPubliclyAccessible,
            };
        }

        var template = BuildDisplayTemplate(propertyType, slots, diagnostics);
        return new AnonymousPropertySpec
        {
            Name = property.Name,
            Key = property.Name + ":" + template,
            MangledName = MangleType(propertyType, slots, diagnostics),
            DisplayTemplate = template,
            IsDynamic = propertyType is IDynamicTypeSymbol,
            IsPubliclyAccessible = IsPubliclyAccessible(propertyType),
        };
    }

    /// <summary>
    /// Map a nested anonymous type's type parameters (in canonical first-appearance
    /// order over its property types) to enclosing-shape slots, assigning new
    /// enclosing slots on first appearance.
    /// </summary>
    private static IReadOnlyList<int> BindParamsToSlots(
        INamedTypeSymbol nestedAnonymous,
        SlotAssigner slots)
    {
        var parameters = new List<ITypeParameterSymbol>();
        foreach (var property in nestedAnonymous.GetMembers()
                     .OfType<IPropertySymbol>()
                     .Where(p => p.Parameters.Length == 0))
        {
            CollectParameterOrder(property.Type, parameters);
        }

        return parameters.Select(slots.GetOrAdd).ToArray();
    }

    /// <summary>
    /// Fully-qualified display text with <c>{slot}</c> placeholders for type
    /// parameters. <c>dynamic</c> is rendered distinctly from
    /// <c>global::System.Object</c> (D2); nested anonymous types are registered and
    /// replaced by their generated class instantiation, so the template is always
    /// anonymous-free.
    /// </summary>
    internal string BuildDisplayTemplate(
        ITypeSymbol type,
        SlotAssigner slots,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
                diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                    "AL010",
                    AnonymousTypeLiftSeverity.Warning,
                    "Anonymous property type could not be resolved (missing reference?); " +
                    "it was replaced with object."));
                return "global::System.Object";

            case ITypeParameterSymbol parameter:
                return "{" + slots.GetOrAdd(parameter) + "}";

            case IArrayTypeSymbol array:
                return BuildDisplayTemplate(array.ElementType, slots, diagnostics)
                    + "[" + new string(',', array.Rank - 1) + "]";

            case IDynamicTypeSymbol:
                // 必须与 global::System.Object 不同键（D2/D9）。
                return "dynamic";

            case INamedTypeSymbol named:
            {
                if (named.IsAnonymousType)
                {
                    // Anonymous type nested inside a generic argument, e.g. List<new{X=int}>.
                    var nested = GetOrCreate(named, diagnostics);
                    var argSlots = BindParamsToSlots(named, slots);
                    return InstantiateShapeDisplay(nested, argSlots);
                }

                var definition = named.OriginalDefinition;
                var name = StripGenericSuffix(definition.MetadataName ?? definition.Name);
                var prefix = QualifiedMetadataPrefix(definition);
                if (named.Arity == 0 || named.TypeArguments.Length == 0)
                {
                    return prefix + name;
                }

                var args = named.TypeArguments
                    .Select(arg => BuildDisplayTemplate(arg, slots, diagnostics));
                return prefix + name + "<" + string.Join(", ", args) + ">";
            }

            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                    "AL010",
                    AnonymousTypeLiftSeverity.Warning,
                    $"Anonymous property type '{type.ToDisplayString()}' has no supported " +
                    "representation; it was replaced with object."));
                return "global::System.Object";

            default:
                diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                    "AL010",
                    AnonymousTypeLiftSeverity.Warning,
                    $"Anonymous property type '{type.ToDisplayString()}' used an unsupported kind " +
                    $"({type.TypeKind}); it was replaced with object."));
                return "global::System.Object";
        }
    }

    internal string MangleType(
        ITypeSymbol type,
        SlotAssigner slots,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "Object";

            case ITypeParameterSymbol parameter:
                return "T" + slots.GetOrAdd(parameter);

            case IArrayTypeSymbol array:
                return MangleType(array.ElementType, slots, diagnostics)
                    + (array.Rank == 1 ? "Array" : "Array" + array.Rank);

            case IDynamicTypeSymbol:
                return "Dynamic";

            case INamedTypeSymbol named:
            {
                if (named.IsAnonymousType)
                {
                    // Ensure registration/order even when only mangle text is needed.
                    var nested = GetOrCreate(named, diagnostics);
                    BindParamsToSlots(named, slots);
                    return nested.Name;
                }

                var name = SanitizeIdentifier(StripGenericSuffix(
                    named.OriginalDefinition.MetadataName ?? named.OriginalDefinition.Name));
                if (named.Arity == 0 || named.TypeArguments.Length == 0)
                {
                    return name;
                }

                var args = named.TypeArguments.Select(a => MangleType(a, slots, diagnostics));
                return name + "_" + string.Join("_", args);
            }

            default:
                return SanitizeIdentifier(type.Name);
        }
    }

    /// <summary>
    /// Collect constraints of one shape's source type parameters (D12): reference/
    /// value/unmanaged/not-null/new() markers plus base-class/interface constraint
    /// types (rendered with the same <c>{slot}</c> templates, but only type
    /// parameters captured by the shape's properties may be referenced — a
    /// constraint type mentioning an uncaptured outer parameter cannot be
    /// represented on the generated class and is dropped; site instantiations stay
    /// valid since dropping a requirement only widens the generated parameter).
    /// <c>AllowsRefLikeType</c> is deliberately ignored — anonymous properties cannot
    /// be ref struct fields, so the marker never applies. Result is ordered by slot.
    /// </summary>
    private List<AnonymousTypeParameterConstraints> BuildConstraints(
        IReadOnlyList<ITypeParameterSymbol> shapeParameters,
        SlotAssigner slots,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        var result = new List<AnonymousTypeParameterConstraints>();
        foreach (var parameter in shapeParameters)
        {
            if (!slots.TryGet(parameter, out var slot))
            {
                slot = slots.GetOrAdd(parameter);
            }

            var typeTemplates = new List<string>();
            foreach (var constraintType in parameter.ConstraintTypes)
            {
                var rendered = TryBuildConstraintTemplate(constraintType, slots, diagnostics);
                if (rendered != null)
                {
                    typeTemplates.Add(rendered);
                }
            }

            if (!parameter.HasReferenceTypeConstraint
                && !parameter.HasValueTypeConstraint
                && !parameter.HasUnmanagedTypeConstraint
                && !parameter.HasNotNullConstraint
                && !parameter.HasConstructorConstraint
                && typeTemplates.Count == 0)
            {
                continue;
            }

            result.Add(new AnonymousTypeParameterConstraints
            {
                Slot = slot,
                HasClass = parameter.HasReferenceTypeConstraint,
                HasStruct = parameter.HasValueTypeConstraint,
                HasUnmanaged = parameter.HasUnmanagedTypeConstraint,
                HasNotNull = parameter.HasNotNullConstraint,
                HasNew = parameter.HasConstructorConstraint,
                ConstraintTypeTemplates = typeTemplates,
            });
        }

        return result;
    }

    /// <summary>
    /// Render a constraint type template. Returns null when the type mentions a type
    /// parameter that was not captured by the shape (no slot to bind to), in which
    /// case the constraint is dropped rather than emitted uncompilable.
    /// </summary>
    private static string? TryBuildConstraintTemplate(
        ITypeSymbol type,
        SlotAssigner slots,
        List<AnonymousTypeLiftDiagnostic> diagnostics)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                return slots.TryGet(parameter, out var slot) ? "{" + slot + "}" : null;

            case IArrayTypeSymbol array:
            {
                var element = TryBuildConstraintTemplate(array.ElementType, slots, diagnostics);
                return element == null
                    ? null
                    : element + "[" + new string(',', array.Rank - 1) + "]";
            }

            case IDynamicTypeSymbol:
                return "dynamic";

            case INamedTypeSymbol named when !named.IsAnonymousType:
            {
                var definition = named.OriginalDefinition;
                var name = StripGenericSuffix(definition.MetadataName ?? definition.Name);
                var prefix = QualifiedMetadataPrefix(definition);
                if (named.Arity == 0 || named.TypeArguments.Length == 0)
                {
                    return prefix + name;
                }

                var args = new List<string>();
                foreach (var arg in named.TypeArguments)
                {
                    var rendered = TryBuildConstraintTemplate(arg, slots, diagnostics);
                    if (rendered == null)
                    {
                        return null;
                    }

                    args.Add(rendered);
                }

                return prefix + name + "<" + string.Join(", ", args) + ">";
            }

            case IErrorTypeSymbol:
                diagnostics.Add(new AnonymousTypeLiftDiagnostic(
                    "AL010",
                    AnonymousTypeLiftSeverity.Warning,
                    "A generic constraint type could not be resolved (missing reference?); " +
                    "that constraint was omitted from the generated class."));
                return null;

            default:
                // 指针/function pointer/嵌套匿名等不可能出现在源码约束中；忽略。
                return null;
        }
    }

    /// <summary>
    /// True when the type can be named from outside its assembly: public, with a
    /// public containing-type chain, public generic arguments, and array element
    /// types. Type parameters inherit their declaring type's accessibility, so they
    /// pass; <c>dynamic</c> passes as well.
    /// </summary>
    internal static bool IsPubliclyAccessible(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
            case IDynamicTypeSymbol:
                return true;

            case IArrayTypeSymbol array:
                return IsPubliclyAccessible(array.ElementType);

            case INamedTypeSymbol named:
            {
                if (named.IsAnonymousType)
                {
                    return named.GetMembers()
                        .OfType<IPropertySymbol>()
                        .Where(p => p.Parameters.Length == 0)
                        .All(p => IsPubliclyAccessible(p.Type));
                }

                if (named.DeclaredAccessibility != Accessibility.Public)
                {
                    return false;
                }

                // 嵌套类型的可访问性还受外层类型约束。
                for (var containing = named.ContainingType; containing != null; containing = containing.ContainingType)
                {
                    if (containing.DeclaredAccessibility != Accessibility.Public)
                    {
                        return false;
                    }
                }

                return named.TypeArguments.All(IsPubliclyAccessible);
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 完整限定名文本：嵌套类型必须带外层类型链（例如 System.HexConverter.Casing）。
    /// </summary>
    internal static string QualifiedMetadataPrefix(INamedTypeSymbol definition)
    {
        var chain = new List<string>();
        for (var containing = definition.ContainingType; containing != null; containing = containing.ContainingType)
        {
            chain.Insert(0, StripGenericSuffix(containing.MetadataName ?? containing.Name));
        }

        var ns = definition.ContainingNamespace;
        var namespaceText = ns is { IsGlobalNamespace: true } || ns == null
            ? string.Empty
            : ns.ToDisplayString() + ".";
        var typeText = chain.Count == 0 ? string.Empty : string.Join(".", chain) + ".";
        return "global::" + namespaceText + typeText;
    }

    internal static string SanitizeIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "Object";
        }

        var chars = text.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        if (chars.Length == 0 || (!char.IsLetter(chars[0]) && chars[0] != '_'))
        {
            return "_" + new string(chars);
        }

        return new string(chars);
    }

    private static string StripGenericSuffix(string metadataName)
    {
        var index = metadataName.LastIndexOf('`');
        return index < 0 ? metadataName : metadataName[..index];
    }

    /// <summary>
    /// Distinct type parameters in structural walk order (properties left-to-right,
    /// recursing into nested anonymous types, arrays and generic arguments) — the
    /// same order slots are assigned in, so positional bindings always line up.
    /// </summary>
    internal static void CollectParameterOrder(ITypeSymbol type, List<ITypeParameterSymbol> into)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                if (!into.Contains(parameter, SymbolEqualityComparer.Default))
                {
                    into.Add(parameter);
                }

                return;

            case IArrayTypeSymbol array:
                CollectParameterOrder(array.ElementType, into);
                return;

            case IDynamicTypeSymbol:
                // dynamic carries no type-parameter slots.
                return;

            case INamedTypeSymbol named:
                if (named.IsAnonymousType)
                {
                    foreach (var property in named.GetMembers()
                                 .OfType<IPropertySymbol>()
                                 .Where(p => p.Parameters.Length == 0))
                    {
                        CollectParameterOrder(property.Type, into);
                    }

                    return;
                }

                foreach (var arg in named.TypeArguments)
                {
                    CollectParameterOrder(arg, into);
                }

                return;
        }
    }

    private string MakeUniqueName(IReadOnlyList<string> nameFragments)
    {
        var baseName = _namePrefix + string.Concat(nameFragments);
        if (baseName.Length > 480)
        {
            baseName = baseName[..480];
        }

        var candidate = baseName;
        var counter = 2;
        while (_takenNames.Contains(candidate))
        {
            candidate = baseName + "_" + counter;
            counter++;
        }

        _takenNames.Add(candidate);
        return candidate;
    }

    /// <summary>
    /// Render a class instantiation for a shape whose generic parameters bind to the
    /// given (enclosing) slots. Slot ids index the enclosing shape's parameters.
    /// </summary>
    internal static string InstantiateShapeDisplay(AnonymousShape shape, IReadOnlyList<int> argSlots)
    {
        if (shape.GenericArity == 0)
        {
            return shape.Name;
        }

        var args = Enumerable.Range(0, shape.GenericArity)
            .Select(i => i < argSlots.Count ? "{" + argSlots[i] + "}" : "{0}");
        return shape.Name + "<" + string.Join(", ", args) + ">";
    }

    /// <summary>
    /// Shape key (D2): <c>(PName:typeKey;...)</c> in declaration order (property
    /// names are case sensitive and the sequence is not sorted) followed by the
    /// normalized per-slot constraint signature (D12), e.g.
    /// <c>|C{s0:class,new};{s1:global::System.IComparable&lt;{1}&gt;}</c>. Two
    /// projections with identical structure but different constraints must produce
    /// distinct classes, otherwise one site's struct/class constraint would make the
    /// other site's instantiation fail to compile; same key / different name is
    /// resolved deterministically via <see cref="MakeUniqueName"/> (_2 suffix).
    /// </summary>
    private static string BuildKey(
        IReadOnlyList<AnonymousPropertySpec> properties,
        IReadOnlyList<AnonymousTypeParameterConstraints> constraints)
    {
        var key = "(" + string.Join(";", properties.Select(p => p.Key)) + ")";
        if (constraints.Count == 0)
        {
            return key;
        }

        var parts = new List<string>();
        foreach (var constraint in constraints)
        {
            var tokens = new List<string>();
            if (constraint.HasClass)
            {
                tokens.Add("class");
            }

            if (constraint.HasStruct)
            {
                tokens.Add("struct");
            }

            if (constraint.HasUnmanaged)
            {
                tokens.Add("unmanaged");
            }

            if (constraint.HasNotNull)
            {
                tokens.Add("notnull");
            }

            tokens.AddRange(constraint.ConstraintTypeTemplates
                .OrderBy(t => t, StringComparer.Ordinal));

            if (constraint.HasNew)
            {
                tokens.Add("new");
            }

            parts.Add("{s" + constraint.Slot + ":" + string.Join(",", tokens) + "}");
        }

        return key + "|C" + string.Join(";", parts);
    }
}
