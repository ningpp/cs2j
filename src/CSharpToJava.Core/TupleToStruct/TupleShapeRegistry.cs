using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.TupleToStruct;

/// <summary>
/// Assigns sequential slot numbers to type parameters on first appearance. Slot
/// numbers are dense (0..n-1) in structural first-appearance order, which makes them
/// canonical: structurally identical open-generic shapes number their parameters
/// identically regardless of which method/class declares them.
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
}

/// <summary>One element of a tuple shape: closed/open type, type-parameter slot, or nested tuple.</summary>
internal sealed record TupleElementSpec
{
    /// <summary>Canonical structural fragment used in the shape key.</summary>
    public required string Key { get; init; }

    /// <summary>Mangled identifier fragment used when composing the struct name.</summary>
    public required string MangledName { get; init; }

    /// <summary>
    /// Fully-qualified display text for the generated field, with <c>{slot}</c>
    /// placeholders (slot numbers are canonical for the enclosing shape) replaced by
    /// the struct's own generic parameters at generation time. Tuples appearing
    /// anywhere inside (element, generic argument, array element) are already
    /// replaced by their generated struct instantiation, so the template is
    /// tuple-free.
    /// </summary>
    public required string FieldDisplayTemplate { get; init; }

    /// <summary>Set when the element is itself a tuple; its struct instantiation.</summary>
    public TupleShape? NestedShape { get; init; }

    /// <summary>For nested tuples: nested generic parameter i binds to this outer slot.</summary>
    public IReadOnlyList<int> NestedArgSlots { get; init; } = Array.Empty<int>();

    public bool IsTuple => NestedShape != null;

    /// <summary>
    /// False when the element type is not publicly accessible outside its assembly
    /// (internal/protected/private type or containing type). Such a shape must be
    /// generated as an internal struct, otherwise C# reports CS0052/CS0051.
    /// </summary>
    public bool IsPubliclyAccessible { get; init; } = true;
}

/// <summary>
/// A registered tuple shape: the structural identity of a C# tuple type plus the name
/// of the struct generated for it. Shapes are deduplicated by canonical key so
/// identical element-type shapes map to one struct project-wide.
/// </summary>
internal sealed class TupleShape
{
    /// <summary>Canonical structural key, e.g. <c>(Int32;String)</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Logical element count (8+ tuples count every logical element).</summary>
    public int Arity { get; init; }

    public IReadOnlyList<TupleElementSpec> Elements { get; init; } = Array.Empty<TupleElementSpec>();

    /// <summary>Generated struct simple name (open shapes include T0/T1 fragments).</summary>
    public required string Name { get; init; }

    /// <summary>Number of generic parameters on the generated struct.</summary>
    public int GenericArity { get; init; }

    /// <summary>For arity &gt;= 8: shape of the <c>Rest</c> tail struct (elements 8..N).</summary>
    public TupleShape? Tail { get; init; }

    /// <summary>For arity &gt;= 8: tail generic parameter i binds to this outer slot.</summary>
    public IReadOnlyList<int> TailArgSlots { get; init; } = Array.Empty<int>();

    /// <summary>
    /// True when every element type (recursively) is public, so the generated struct
    /// can be public. Otherwise the struct is generated as internal.
    /// </summary>
    public bool IsPubliclyAccessible { get; init; } = true;
}

/// <summary>
/// Builds, names, and deduplicates tuple shapes across the whole compilation. The
/// registry is shared between the analysis pass (registers shapes from every tree)
/// and the rewrite pass (looks shapes up when emitting struct names), keeping struct
/// naming consistent across files.
///
/// Slot numbering is per-shape (see <see cref="SlotAssigner"/>): a shape's key uses
/// its own dense numbering, while nested tuple instantiations embedded in an outer
/// shape's templates reference the outer shape's slots positionally.
/// </summary>
internal sealed partial class TupleShapeRegistry
{
    private readonly Dictionary<string, TupleShape> _shapesByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _takenNames = new(StringComparer.Ordinal);
    private readonly List<TupleShape> _ordered = new();
    private readonly string _namePrefix;

    public TupleShapeRegistry(string namePrefix = "Tuple")
    {
        _namePrefix = namePrefix;
    }

    /// <summary>Shapes in deterministic registration order (nested/tails first).</summary>
    public IReadOnlyList<TupleShape> Shapes => _ordered;

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

    public TupleShape GetOrCreateShape(
        INamedTypeSymbol tupleType,
        List<TupleToStructDiagnostic> diagnostics)
        => GetOrCreateShape(tupleType.TupleElements.Select(f => f.Type).ToArray(), new SlotAssigner(), diagnostics);

    /// <summary>
    /// Get or create the shape for a logical element-type list. <paramref name="slots"/>
    /// is the canonical assigner for the shape being built; nested shapes get their own
    /// canonical numbering and are bound back positionally via their arg slots.
    /// </summary>
    public TupleShape GetOrCreateShape(
        IReadOnlyList<ITypeSymbol> elementTypes,
        SlotAssigner slots,
        List<TupleToStructDiagnostic> diagnostics)
    {
        var elements = new List<TupleElementSpec>(elementTypes.Count);
        var mangledNames = new List<string>(elementTypes.Count);
        foreach (var elementType in elementTypes)
        {
            var spec = BuildElementSpec(elementType, slots, diagnostics);
            elements.Add(spec);
            mangledNames.Add(spec.MangledName);
        }

        var key = "(" + string.Join(";", elements.Select(e => e.Key)) + ")";
        if (_shapesByKey.TryGetValue(key, out var existing))
        {
            return existing;
        }

        TupleShape? tail = null;
        IReadOnlyList<int> tailArgSlots = Array.Empty<int>();
        if (elements.Count > 7)
        {
            // ValueTuple encodes arity >= 8 as T1..T7 + TRest (nested ValueTuple).
            // The generated struct mirrors that layout: Item1..Item7 plus a Rest
            // field typed as the struct for elements 8..N.
            var tailTypes = elementTypes.Skip(7).ToArray();
            var tailShape = GetOrCreateShape(tailTypes, new SlotAssigner(), diagnostics);
            var tailParams = new List<ITypeParameterSymbol>();
            foreach (var t in tailTypes)
            {
                CollectParameterOrder(t, tailParams);
            }

            tail = tailShape;
            tailArgSlots = tailParams.Select(slots.GetOrAdd).ToArray();
        }

        var publiclyAccessible = elements.All(e => e.IsPubliclyAccessible)
            && (tail?.IsPubliclyAccessible ?? true);
        var shape = new TupleShape
        {
            Key = key,
            Arity = elements.Count,
            Elements = elements,
            Name = MakeUniqueName(mangledNames),
            GenericArity = slots.Count,
            Tail = tail,
            TailArgSlots = tailArgSlots,
            IsPubliclyAccessible = publiclyAccessible,
        };
        _shapesByKey.Add(key, shape);
        _ordered.Add(shape);
        return shape;
    }

    public TupleShape? FindByKey(string key) =>
        _shapesByKey.TryGetValue(key, out var shape) ? shape : null;

    private TupleElementSpec BuildElementSpec(
        ITypeSymbol elementType,
        SlotAssigner slots,
        List<TupleToStructDiagnostic> diagnostics)
    {
        if (elementType is INamedTypeSymbol { IsTupleType: true } nestedTuple)
        {
            var nestedShape = GetOrCreateShape(
                nestedTuple.TupleElements.Select(f => f.Type).ToArray(),
                new SlotAssigner(),
                diagnostics);
            var argSlots = BindParamsToSlots(nestedTuple.TupleElements.Select(f => f.Type), slots);
            return new TupleElementSpec
            {
                Key = "TUPLE" + nestedShape.Key + "[" + string.Join(",", argSlots) + "]",
                MangledName = nestedShape.Name,
                FieldDisplayTemplate = InstantiateShapeDisplay(nestedShape, argSlots),
                NestedShape = nestedShape,
                NestedArgSlots = argSlots,
                IsPubliclyAccessible = nestedShape.IsPubliclyAccessible,
            };
        }

        var template = BuildDisplayTemplate(elementType, slots, diagnostics);
        return new TupleElementSpec
        {
            Key = template,
            MangledName = MangleType(elementType, slots),
            FieldDisplayTemplate = template,
            IsPubliclyAccessible = IsPubliclyAccessible(elementType),
        };
    }

    /// <summary>
    /// Map a nested structure's type parameters (in canonical first-appearance order)
    /// to enclosing-shape slots, assigning new enclosing slots on first appearance.
    /// </summary>
    private static IReadOnlyList<int> BindParamsToSlots(
        IEnumerable<ITypeSymbol> elementTypes,
        SlotAssigner slots)
    {
        var parameters = new List<ITypeParameterSymbol>();
        foreach (var type in elementTypes)
        {
            CollectParameterOrder(type, parameters);
        }

        return parameters.Select(slots.GetOrAdd).ToArray();
    }

    /// <summary>
    /// Fully-qualified display text with <c>{slot}</c> placeholders for type
    /// parameters. Tuples nested in generic arguments are registered and replaced by
    /// their generated struct instantiation, so the template is always tuple-free.
    /// </summary>
    internal string BuildDisplayTemplate(
        ITypeSymbol type,
        SlotAssigner slots,
        List<TupleToStructDiagnostic> diagnostics)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
                diagnostics.Add(new TupleToStructDiagnostic(
                    TupleToStructSeverity.Warning,
                    "Tuple element type could not be resolved (missing reference?); " +
                    "it was replaced with object."));
                return "global::System.Object";

            case ITypeParameterSymbol parameter:
                return "{" + slots.GetOrAdd(parameter) + "}";

            case IArrayTypeSymbol array:
                return BuildDisplayTemplate(array.ElementType, slots, diagnostics)
                    + "[" + new string(',', array.Rank - 1) + "]";

            case IDynamicTypeSymbol:
                return "global::System.Object";

            case INamedTypeSymbol named:
            {
                if (named.IsTupleType)
                {
                    // Tuple nested inside a generic argument, e.g. List<(int, int)>.
                    var elementTypes = named.TupleElements.Select(f => f.Type).ToArray();
                    var nested = GetOrCreateShape(elementTypes, new SlotAssigner(), diagnostics);
                    var argSlots = BindParamsToSlots(elementTypes, slots);
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
                diagnostics.Add(new TupleToStructDiagnostic(
                    TupleToStructSeverity.Warning,
                    $"Tuple element type '{type.ToDisplayString()}' has no Java representation; " +
                    "it was replaced with object."));
                return "global::System.Object";

            default:
                diagnostics.Add(new TupleToStructDiagnostic(
                    TupleToStructSeverity.Warning,
                    $"Tuple element type '{type.ToDisplayString()}' used an unsupported kind " +
                    $"({type.TypeKind}); it was replaced with object."));
                return "global::System.Object";
        }
    }

    internal static string MangleType(ITypeSymbol type, SlotAssigner slots)
    {
        switch (type)
        {
            case IErrorTypeSymbol:
                return "Object";

            case ITypeParameterSymbol parameter:
                return "T" + slots.GetOrAdd(parameter);

            case IArrayTypeSymbol array:
                return MangleType(array.ElementType, slots)
                    + (array.Rank == 1 ? "Array" : "Array" + array.Rank);

            case IDynamicTypeSymbol:
                return "Object";

            case INamedTypeSymbol named:
            {
                if (named.IsTupleType)
                {
                    // Tuple nested in a generic argument; the fragment only needs to
                    // be structurally deterministic.
                    return named.TupleElements.Aggregate(
                        "Tuple",
                        (acc, f) => acc + MangleType(f.Type, slots));
                }

                var name = SanitizeIdentifier(StripGenericSuffix(
                    named.OriginalDefinition.MetadataName ?? named.OriginalDefinition.Name));
                if (named.Arity == 0 || named.TypeArguments.Length == 0)
                {
                    return name;
                }

                var args = named.TypeArguments.Select(a => MangleType(a, slots));
                return name + "_" + string.Join("_", args);
            }

            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                return "Object";

            default:
                return SanitizeIdentifier(type.Name);
        }
    }

    /// <summary>
    /// True when the type can be named from outside its assembly: public, with a public
    /// containing-type chain, public generic arguments, and array element types.
    /// Type parameters inherit their declaring type's accessibility, so they pass.
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
                if (named.IsTupleType)
                {
                    return named.TupleElements.All(f => IsPubliclyAccessible(f.Type));
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
    /// Distinct type parameters in structural walk order (elements left-to-right,
    /// recursing into nested tuples and generic arguments) — the same order slots are
    /// assigned in, so positional bindings always line up.
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

            case INamedTypeSymbol named:
                if (named.IsTupleType)
                {
                    foreach (var field in named.TupleElements)
                    {
                        CollectParameterOrder(field.Type, into);
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

    private string MakeUniqueName(IReadOnlyList<string> mangledNames)
    {
        var baseName = _namePrefix + string.Concat(mangledNames);
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
    /// Render a struct instantiation for a shape whose generic parameters bind to the
    /// given (enclosing) slots. Slot ids index the enclosing shape's parameters.
    /// </summary>
    internal static string InstantiateShapeDisplay(TupleShape shape, IReadOnlyList<int> argSlots)
    {
        if (shape.GenericArity == 0)
        {
            return shape.Name;
        }

        var args = Enumerable.Range(0, shape.GenericArity)
            .Select(i => i < argSlots.Count ? "{" + argSlots[i] + "}" : "{0}");
        return shape.Name + "<" + string.Join(", ", args) + ">";
    }
}
