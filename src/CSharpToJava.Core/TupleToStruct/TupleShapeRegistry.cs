using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.TupleToStruct;

/// <summary>One element slot of a generated tuple struct.</summary>
internal sealed record TupleElementSpec
{
    /// <summary>Canonical structural fragment used in the shape key.</summary>
    public required string Key { get; init; }

    /// <summary>Mangled identifier fragment used when composing the struct name.</summary>
    public required string MangledName { get; init; }

    /// <summary>
    /// Field type template: the slot placeholder <c>{i}</c>, replaced by the struct's
    /// own generic parameter at generation time. Element types are supplied by the
    /// use site as type arguments, so the template never names a concrete type.
    /// </summary>
    public required string FieldDisplayTemplate { get; init; }

    /// <summary>
    /// Named tuple element names observed for this position (e.g. <c>Char</c> for
    /// <c>(char Char, …)</c>). The generated struct exposes one read/write property
    /// per name so element accesses the semantic model cannot see — the ones inside
    /// a conditional-compilation branch this pass' parse options disable — keep
    /// binding after the declaration site was lowered to the struct.
    /// </summary>
    public HashSet<string> AliasNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A registered tuple shape: the structural identity of a C# tuple type plus the name
/// of the struct generated for it.
///
/// 形状身份按 <b>逻辑元数</b> 归一：同一元数的所有元组（<c>(TKey, TValue)</c>、
/// <c>(string, int)</c>、<c>(T, T)</c> …）共用同一个泛型 struct 定义，元素类型在
/// 站点上按位置作为类型实参传入。否则开放站点与具体站点会各自生成一个类型
/// （例如 <c>TupleT0T1&lt;string, int&gt;</c> 与 <c>TupleStringInt32</c>），
/// 库侧签名与调用侧无法互相赋值（CS1503/CS0266）。
/// </summary>
internal sealed class TupleShape
{
    /// <summary>Canonical structural key, e.g. <c>({0};{1})</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Logical element count (8+ tuples count every logical element).</summary>
    public int Arity { get; init; }

    public IReadOnlyList<TupleElementSpec> Elements { get; init; } = Array.Empty<TupleElementSpec>();

    /// <summary>Generated struct simple name (one per logical arity, e.g. TupleT0T1).</summary>
    public required string Name { get; init; }

    /// <summary>Number of generic parameters on the generated struct (one per logical element).</summary>
    public int GenericArity { get; init; }

    /// <summary>For arity &gt;= 8: shape of the <c>Rest</c> tail struct (elements 8..N).</summary>
    public TupleShape? Tail { get; init; }

    /// <summary>For arity &gt;= 8: tail generic parameter i binds to this outer slot.</summary>
    public IReadOnlyList<int> TailArgSlots { get; init; } = Array.Empty<int>();
}

/// <summary>
/// Builds, names, and deduplicates tuple shapes across the whole compilation. The
/// registry is shared between the analysis pass (registers shapes from every tree)
/// and the rewrite pass (looks shapes up when emitting struct names), keeping struct
/// naming consistent across files.
///
/// 每个逻辑元数只有一个定义，槽位编号即元素位置（0..n-1）：这让结构相同的元组
/// 无论在哪个方法/类里、以开放还是封闭形态出现，都解析到同一个 struct。
/// </summary>
internal sealed partial class TupleShapeRegistry
{
    private readonly Dictionary<int, TupleShape> _shapesByArity = new();
    private readonly HashSet<string> _takenNames = new(StringComparer.Ordinal);
    private readonly List<TupleShape> _ordered = new();
    private readonly string _namePrefix;

    public TupleShapeRegistry(string namePrefix = "Tuple")
    {
        _namePrefix = namePrefix;
    }

    /// <summary>Shapes in deterministic registration order (tails first).</summary>
    public IReadOnlyList<TupleShape> Shapes => _ordered;

    public TupleShape GetOrCreateShape(INamedTypeSymbol tupleType)
        => GetOrCreateShape(
            tupleType.TupleElements.Select(f => f.Type).ToArray(),
            tupleType.TupleElements.Select(f => f.Name).ToArray());

    /// <summary>
    /// Get or create the shape for a logical element-type list. Only the element count
    /// participates in the identity: the element types themselves are rendered as type
    /// arguments at each use site (see <c>ShapeDisplayInScope</c>), so an open
    /// <c>(T1, T2)</c> and a closed <c>(string, int)</c> resolve to the same struct.
    /// <paramref name="elementNames"/> carries the source tuple's named elements (null
    /// or empty entries for unnamed ones) so the generated struct can expose them.
    /// </summary>
    public TupleShape GetOrCreateShape(
        IReadOnlyList<ITypeSymbol> elementTypes,
        IReadOnlyList<string>? elementNames)
    {
        var shape = GetOrCreateCanonicalShape(elementTypes.Count);
        RecordAliasNames(shape, elementNames);
        return shape;
    }

    /// <summary>
    /// Canonical shape for a logical arity: slots 0..n-1 in element order, no
    /// constraints (fields are the struct's own generic parameters, never constrained
    /// constructions), tail chained exactly like <c>System.ValueTuple</c> for 8+.
    /// </summary>
    private TupleShape GetOrCreateCanonicalShape(int arity)
    {
        if (_shapesByArity.TryGetValue(arity, out var existing))
        {
            return existing;
        }

        var elements = new List<TupleElementSpec>(arity);
        var mangledNames = new List<string>(arity);
        for (var i = 0; i < arity; i++)
        {
            elements.Add(new TupleElementSpec
            {
                Key = "{" + i + "}",
                MangledName = "T" + i,
                FieldDisplayTemplate = "{" + i + "}",
            });
            mangledNames.Add("T" + i);
        }

        TupleShape? tail = null;
        IReadOnlyList<int> tailArgSlots = Array.Empty<int>();
        if (arity > 7)
        {
            // ValueTuple encodes arity >= 8 as T1..T7 + TRest (nested ValueTuple).
            // The generated struct mirrors that layout: Item1..Item7 plus a Rest
            // field typed as the canonical struct for elements 8..N.
            tail = GetOrCreateCanonicalShape(arity - 7);
            tailArgSlots = Enumerable.Range(7, arity - 7).ToArray();
        }

        var shape = new TupleShape
        {
            Key = "(" + string.Join(";", elements.Select(static e => e.Key)) + ")",
            Arity = arity,
            Elements = elements,
            Name = MakeUniqueName(mangledNames),
            GenericArity = arity,
            Tail = tail,
            TailArgSlots = tailArgSlots,
        };
        _shapesByArity.Add(arity, shape);
        _ordered.Add(shape);
        return shape;
    }

    /// <summary>
    /// Records the named elements of every site that reuses a shape: identical
    /// arities may be spelled with different element names (and a later site may
    /// name elements the first one left unnamed), so the generated struct must
    /// expose the union of the observed names.
    /// </summary>
    private static void RecordAliasNames(TupleShape shape, IReadOnlyList<string>? elementNames)
    {
        if (elementNames == null)
        {
            return;
        }

        var count = Math.Min(elementNames.Count, Math.Min(shape.Elements.Count, 7));
        for (var i = 0; i < count; i++)
        {
            var name = elementNames[i];
            if (!string.IsNullOrWhiteSpace(name))
            {
                shape.Elements[i].AliasNames.Add(name);
            }
        }
    }

    public TupleShape? FindByKey(string key) =>
        _ordered.FirstOrDefault(shape => shape.Key == key);

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

                // 元素物化为 System.Object（见 DisplayTypeInScope），匿名类型本身
                // 的 internal 可访问性不影响生成字段。
                if (named.IsAnonymousType)
                {
                    return true;
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
    /// True when <paramref name="type"/> can be referenced by a top-level
    /// (non-nested) type generated IN THE SAME ASSEMBLY as the tuple use site.
    /// The generated tuple struct is a top-level internal type, so:
    /// public/internal types and <c>protected internal</c> (assembly-wide) are
    /// visible; <c>private</c>, <c>protected</c> and <c>private protected</c>
    /// nested types are not, even from another internal type in the assembly.
    /// Recurses into tuple elements, generic arguments and array element types.
    /// Type parameters, dynamic, pointers/error types (materialized to object)
    /// and anonymous types (also materialized to object) pass.
    /// </summary>
    internal static bool IsEmittableFromTopLevel(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
            case IDynamicTypeSymbol:
            case IErrorTypeSymbol:
            case IPointerTypeSymbol:
            case IFunctionPointerTypeSymbol:
                return true;

            case IArrayTypeSymbol array:
                return IsEmittableFromTopLevel(array.ElementType);

            case INamedTypeSymbol named:
            {
                if (named.IsTupleType)
                {
                    foreach (var field in named.TupleElements)
                    {
                        if (!IsEmittableFromTopLevel(field.Type))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                // 元素物化为 System.Object（见 DisplayTypeInScope），其本身的
                // 可访问性不影响生成字段。
                if (named.IsAnonymousType)
                {
                    return true;
                }

                // 嵌套类型的有效可访问性是自身与各外层类型的最严值：链条上任一
                // 层为 private/protected/private-protected，顶级 struct 就不可见。
                for (var containing = named; containing != null; containing = containing.ContainingType)
                {
                    if (!VisibleAtAssemblyTopLevel(containing.DeclaredAccessibility))
                    {
                        return false;
                    }
                }

                foreach (var argument in named.TypeArguments)
                {
                    if (!IsEmittableFromTopLevel(argument))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                // DisplayTypeInScope 会把不支持的种类物化为 System.Object。
                return true;
        }
    }

    /// <summary>
    /// Accessibilities visible to a non-derived top-level type in the same
    /// assembly: public, internal, <c>protected internal</c> (InternalOrProtected)
    /// and NotApplicable (e.g. namespaces). private/protected/private-protected
    /// fail.
    /// </summary>
    private static bool VisibleAtAssemblyTopLevel(Accessibility accessibility) =>
        accessibility is Accessibility.Public
            or Accessibility.Internal
            or Accessibility.ProtectedOrInternal
            or Accessibility.NotApplicable;

    /// <summary>
    /// 完整限定名文本：嵌套类型必须带外层类型链（例如 System.HexConverter.Casing）。
    /// 外层类型是泛型类型时还必须带上它的泛型实参（<c>Outer&lt;T&gt;.Inner</c>）：
    /// 省略实参的 <c>Outer.Inner</c> 绑定到同名非泛型类型的嵌套成员上，C# 报
    /// CS0426。实参由 <paramref name="renderContainingArgument"/> 渲染（可能是类型
    /// 参数槽位占位符或源码作用域内的类型名）；任一实参无法渲染时返回 null。
    /// </summary>
    internal static string? QualifiedMetadataPrefix(
        INamedTypeSymbol named,
        Func<ITypeSymbol, string?> renderContainingArgument)
    {
        var chain = new List<string>();
        for (var containing = named.ContainingType; containing != null; containing = containing.ContainingType)
        {
            var name = StripGenericSuffix(containing.MetadataName ?? containing.Name);
            if (containing.TypeArguments.Length > 0)
            {
                var arguments = new List<string>(containing.TypeArguments.Length);
                foreach (var argument in containing.TypeArguments)
                {
                    if (renderContainingArgument(argument) is not { } text)
                    {
                        return null;
                    }

                    arguments.Add(text);
                }

                name += "<" + string.Join(", ", arguments) + ">";
            }

            chain.Insert(0, name);
        }

        var ns = named.ContainingNamespace;
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
}
