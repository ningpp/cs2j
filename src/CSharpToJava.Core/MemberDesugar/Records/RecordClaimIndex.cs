using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Properties;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// 每编译一次的 with 站点分类与「整类型级」认领结论（PropertyRule 5124 让位钩与
/// RecordRule 共用同一实例同一结论，杜绝两侧判定分歧——开放问题 6 裁决）。
/// 结论全部来自 SemanticModel 符号绑定。编译内无任何 WithExpressionSyntax 时
/// 返回 <see cref="IsEmpty"/> 空索引（零成本，规则零 plan）。
/// </summary>
internal sealed class RecordClaimIndex
{
    private static readonly ConditionalWeakTable<Compilation, RecordClaimIndex> _cache = new();

    private readonly Dictionary<string, SiteClaim> _sitesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, TypeClaim> _typeClaims =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, ComponentInfo> _components =
        new(StringComparer.Ordinal);

    public bool IsEmpty => _sitesByKey.Count == 0;
    public IReadOnlyCollection<SiteClaim> Sites => _sitesByKey.Values;
    public IReadOnlyCollection<ComponentInfo> Components => _components.Values;

    public static RecordClaimIndex For(CSharpCompilation compilation)
        => _cache.GetValue(compilation, Build);

    private static RecordClaimIndex Build(Compilation compilationArg)
    {
        var compilation = (CSharpCompilation)compilationArg;
        var index = new RecordClaimIndex();

        // ---- 1. 逐站点纯本地分类（不依赖跨类型闭包）----
        var rawSites = new List<SiteClaim>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.TryGetRoot(out var root)) continue;
            if (!root.DescendantNodes().Any(n => n is WithExpressionSyntax)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var w in root.DescendantNodes().OfType<WithExpressionSyntax>())
                rawSites.Add(ClassifySite(w, model));
        }
        if (rawSites.Count == 0) return index;

        foreach (var site in rawSites)
            index._sitesByKey[SiteKey(site.FilePath, site.SpanStart)] = site;

        // ---- 2. 源内 record 全集 + 元数据 record 全集（树外派生探测）----
        // 全部归一 OriginalDefinition（构造形 Box<int> 与定义形 Box<T> 去重）。
        var sourceRecords = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var s in rawSites)
        {
            if (s.ReceiverType is { } t && CloneCtorResolver.IsInSource(t) && t.IsRecord)
                sourceRecords.Add(t.OriginalDefinition);
        }
        // 闭包扩展需要源内全部 record（兄弟/树内派生），从语法树补全（partial 按符号去重）。
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.TryGetRoot(out var root)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var rec in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(rec) is { } t) sourceRecords.Add(t.OriginalDefinition);
            }
        }

        var allRecords = CloneCtorResolver.EnumerateAllRecords(compilation);

        // ---- 3. 接收者类型分组（仅源内 record class 有可行站点候选资格）----
        // 键统一归一 OriginalDefinition：Box<int>/Box<string> 同一声明同一 plan/组件。
        var sitesByType = new Dictionary<INamedTypeSymbol, List<SiteClaim>>(
            SymbolEqualityComparer.Default);
        foreach (var site in rawSites)
        {
            if (site.ReceiverType is not { } type) continue;
            if (!CloneCtorResolver.IsInSource(type)) continue;
            type = type.OriginalDefinition;
            if (!sitesByType.TryGetValue(type, out var list))
                sitesByType[type] = list = new List<SiteClaim>();
            list.Add(site);
        }

        // 候选：源内显式成员 record class，且至少一个站点本地分类通过。
        var candidates = sitesByType
            .Where(kv => CloneCtorResolver.IsRecordClass(kv.Key)
                && !CloneCtorResolver.IsPositional(kv.Key)
                && kv.Value.Any(s => s.IsLocallyRewritable))
            .Select(kv => kv.Key)
            .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // ---- 4. 继承链闭包（候选 + record 祖先 + 源内 record 派生）+ 并查集 ----
        // 组件全部以 OriginalDefinition 为键（构造泛型归并到定义形）。
        var dsu = new Dsu();
        foreach (var candidate in candidates)
        {
            dsu.Add(candidate);
            foreach (var a in CloneCtorResolver.RecordAncestors(candidate))
                dsu.Union(candidate, a.OriginalDefinition);
            foreach (var d in CloneCtorResolver.SourceRecordDescendants(candidate, sourceRecords))
                dsu.Union(candidate, d.OriginalDefinition);
        }

        // ---- 5. 组件成员聚合 + 组件级闸门：闭包含位置 record → 全组件 5220 ----
        var components = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var root = dsu.Find(candidate);
            if (!components.TryGetValue(root, out var component))
                components[root] = component = new ComponentInfo(root);
            component.Members.Add(candidate);
            foreach (var a in CloneCtorResolver.RecordAncestors(candidate))
                component.Members.Add(a.OriginalDefinition);
            foreach (var d in CloneCtorResolver.SourceRecordDescendants(candidate, sourceRecords))
                component.Members.Add(d.OriginalDefinition);
        }
        foreach (var component in components.Values.ToList())
        {
            var positional = component.Members.FirstOrDefault(CloneCtorResolver.IsPositional);
            if (positional != null)
            {
                component.GateCode = MemberDesugarDiagnosticCode.PositionalReshapeGate;
                component.GateReason = RecordDiagnostics.PositionalScheduled(
                    positional.ToDisplayString());
            }
        }
        foreach (var component in components.Values)
            index._components[component.Root] = component;

        // ---- 6. 逐候选类型定案 ----
        foreach (var type in candidates)
        {
            var ownSites = sitesByType[type];
            var componentRoot = dsu.Find(type);

            var bad = ownSites.FirstOrDefault(s => !s.IsLocallyRewritable);
            if (bad != null)
            {
                index._typeClaims[type] = new TypeClaim(
                    false, bad.FailCode!.Value, bad.FailReason!, componentRoot);
                continue;
            }

            if (components[componentRoot].GateCode is { } gateCode)
            {
                index._typeClaims[type] = new TypeClaim(
                    false, gateCode, components[componentRoot].GateReason!, componentRoot);
                continue;
            }

            var metadataDerived = CloneCtorResolver.FindMetadataDerivedRecords(type, allRecords);
            if (metadataDerived.Count > 0)
            {
                index._typeClaims[type] = new TypeClaim(
                    false, MemberDesugarDiagnosticCode.MetadataDerivedRisk,
                    RecordDiagnostics.MetadataDerivedRisk(type.ToDisplayString(), metadataDerived),
                    componentRoot);
                continue;
            }

            if (CloneCtorResolver.FindCopyConstructor(type) == null)
            {
                index._typeClaims[type] = new TypeClaim(
                    false, MemberDesugarDiagnosticCode.NonRecordClassWith,
                    RecordDiagnostics.CopyConstructorMissing(type.ToDisplayString()),
                    componentRoot);
                continue;
            }

            index._typeClaims[type] = new TypeClaim(true, null, null, componentRoot);
        }

        return index;
    }

    /// <summary>5124 让位钩（只回答：这个 with 初始化器直接赋值会不会被 Records 改写）。</summary>
    public bool IsAssignmentClaimed(AssignmentExpressionSyntax assign, SemanticModel model)
    {
        if (assign.Parent is not InitializerExpressionSyntax init
            || init.Parent is not WithExpressionSyntax w
            || !ReferenceEquals(w.Initializer, init))
            return false;

        if (!_sitesByKey.TryGetValue(SiteKey(w.SyntaxTree.FilePath, w.SpanStart), out var site))
            return false;
        if (!site.IsLocallyRewritable || site.ReceiverType == null) return false;
        if (!_typeClaims.TryGetValue(site.ReceiverType.OriginalDefinition, out var claim)
            || !claim.Feasible)
            return false;

        // 只对可写属性让位（字段不走 C11，5124 与字段无关）。
        if (model.GetSymbolInfo(assign.Left).Symbol is not IPropertySymbol prop) return false;
        return prop.SetMethod != null
            && !PropertyFacts.IsPositionalRecordProperty(prop);
    }

    public IReadOnlyList<WithSite> BuildSites(INamedTypeSymbol? receiverFilter)
    {
        var list = new List<WithSite>();
        foreach (var s in Sites.OrderBy(s => s.FilePath, StringComparer.Ordinal)
                     .ThenBy(s => s.SpanStart))
        {
            if (receiverFilter != null
                && (s.ReceiverType == null
                    || !SymbolEqualityComparer.Default.Equals(
                        s.ReceiverType.OriginalDefinition, receiverFilter.OriginalDefinition)))
                continue;
            list.Add(s.ToWithSite());
        }
        return list;
    }

    public bool TryGetTypeClaim(INamedTypeSymbol type, out TypeClaim claim)
        => _typeClaims.TryGetValue(type, out claim!);

    public bool TryGetComponent(string root, out ComponentInfo component)
        => _components.TryGetValue(root, out component!);

    public IEnumerable<INamedTypeSymbol> ClaimedTypes => _typeClaims.Keys;

    public static string SiteKey(string filePath, int spanStart) => $"{filePath}@{spanStart}";

    // ---- 站点本地分类（单站点，与跨类型闭包无关）----

    private static SiteClaim ClassifySite(WithExpressionSyntax w, SemanticModel model)
    {
        var filePath = w.SyntaxTree.FilePath;
        var span = w.SpanStart;
        var typeFqn = () => model.GetTypeInfo(w).Type?.ToDisplayString();

        // 1) 表达式树（合法 C# 本不允许 with 入表达式树，防御性）。
        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(w, model))
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.ExpressionTreeWith,
                RecordDiagnostics.ExpressionTreeWith());

        // 2) 嵌套 with：内层（祖先 with）与外层（初始化器内含 with）统一 5224（WR-5/M2）。
        if (w.Ancestors().OfType<WithExpressionSyntax>().Any())
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.UnsupportedNestedInitializer,
                RecordDiagnostics.UnsupportedNestedInitializer(
                    typeFqn(), "a nested with-expression (WR-5)"));
        if (w.Initializer != null
            && w.Initializer.DescendantNodes().OfType<WithExpressionSyntax>().Any())
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.UnsupportedNestedInitializer,
                RecordDiagnostics.UnsupportedNestedInitializer(
                    typeFqn(), "a nested with-expression (WR-5)"));

        var type = model.GetTypeInfo(w).Type;

        // 3) dynamic / 绑定失败。
        if (type == null || type.TypeKind == TypeKind.Error)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.NonRecordClassWith,
                RecordDiagnostics.NonRecordClassWith(null, "the receiver type could not be bound"),
                receiverType: null);
        if (type.TypeKind == TypeKind.Dynamic)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.DynamicWith,
                RecordDiagnostics.DynamicWith(), receiverType: null);

        if (type is not INamedTypeSymbol named)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.NonRecordClassWith,
                RecordDiagnostics.NonRecordClassWith(type.ToDisplayString(),
                    $"receiver kind is {type.TypeKind}"), receiverType: null);

        // 4) 匿名类型 / 元组。
        if (named.IsAnonymousType)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.AnonymousTypeWith,
                RecordDiagnostics.AnonymousTypeWith(), receiverType: named);
        if (named.IsTupleType)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.PositionalReshapeGate,
                RecordDiagnostics.ValueCopyScheduled(named.ToDisplayString()),
                receiverType: named);

        // 5) record 形态分流。
        if (named.IsRecord)
        {
            if (!CloneCtorResolver.IsInSource(named))
                return SiteClaim.Fail(w, filePath, span,
                    MemberDesugarDiagnosticCode.MetadataRecordReceiver,
                    RecordDiagnostics.MetadataRecordReceiver(named.ToDisplayString()),
                    receiverType: named);
            if (CloneCtorResolver.IsRecordStruct(named))
                return SiteClaim.Fail(w, filePath, span,
                    MemberDesugarDiagnosticCode.PositionalReshapeGate,
                    RecordDiagnostics.ValueCopyScheduled(named.ToDisplayString()),
                    receiverType: named);
            if (CloneCtorResolver.IsPositional(named))
                return SiteClaim.Fail(w, filePath, span,
                    MemberDesugarDiagnosticCode.PositionalReshapeGate,
                    RecordDiagnostics.PositionalScheduled(named.ToDisplayString()),
                    receiverType: named);
        }
        else if (named.TypeKind == TypeKind.Struct)
        {
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.PositionalReshapeGate,
                RecordDiagnostics.ValueCopyScheduled(named.ToDisplayString()),
                receiverType: named);
        }
        else
        {
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.NonRecordClassWith,
                RecordDiagnostics.NonRecordClassWith(named.ToDisplayString(),
                    "only record class receivers are supported (CS8858 for plain classes)"),
                receiverType: named);
        }

        // 6) 初始化器逐成员绑定（空初始化器 WR-3 直接通过）。
        var assignments = new List<WithAssignment>();
        if (w.Initializer is { Expressions.Count: > 0 } init)
        {
            foreach (var expr in init.Expressions)
            {
                if (expr is not AssignmentExpressionSyntax assign
                    || !expr.IsKind(SyntaxKind.SimpleAssignmentExpression))
                {
                    return FailInitializer(w, filePath, span, named,
                        "a collection/Add-style or non-assignment initializer element");
                }

                // X = { ... } 成员/集合初始化器（右值是裸初始化器表达式）。
                if (assign.Right is InitializerExpressionSyntax)
                    return FailInitializer(w, filePath, span, named,
                        "a nested object/collection member initializer");

                var symbol = model.GetSymbolInfo(assign.Left).Symbol;
                if (symbol is not (IPropertySymbol or IFieldSymbol))
                    return FailInitializer(w, filePath, span, named,
                        "an initializer target that does not bind to a property or field of the receiver");

                if (symbol is IPropertySymbol p)
                {
                    if (p.IsStatic || p.ReturnsByRef)
                        return FailInitializer(w, filePath, span, named,
                            "a static or ref-returning property target");
                    if (PropertyFacts.IsPositionalRecordProperty(p))
                        return SiteClaim.Fail(w, filePath, span,
                            MemberDesugarDiagnosticCode.PositionalReshapeGate,
                            RecordDiagnostics.PositionalScheduled(named.ToDisplayString()),
                            receiverType: named);
                    if (p.SetMethod == null)
                        return FailInitializer(w, filePath, span, named,
                            "a read-only property target");
                    if (!IsMemberOfReceiver(p, named))
                        return FailInitializer(w, filePath, span, named,
                            "a property not declared on the receiver type or its record bases");
                    assignments.Add(new WithAssignment
                    {
                        Member = p.OriginalDefinition,
                        MemberName = p.Name,
                        IsProperty = true,
                        Syntax = assign,
                    });
                }
                else
                {
                    var f = (IFieldSymbol)symbol;
                    if (f.IsStatic)
                        return FailInitializer(w, filePath, span, named,
                            "a static field target");
                    if (!IsMemberOfReceiver(f, named))
                        return FailInitializer(w, filePath, span, named,
                            "a field not declared on the receiver type or its record bases");
                    assignments.Add(new WithAssignment
                    {
                        Member = f.OriginalDefinition,
                        MemberName = f.Name,
                        IsProperty = false,
                        Syntax = assign,
                    });
                }
            }
        }

        var isEmpty = assignments.Count == 0;

        // 7) 非空站点必须可提升（决策 10）。
        if (!isEmpty && !CloneCtorResolver.IsLiftableSite(w))
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.NonLiftableWithContext,
                RecordDiagnostics.NonLiftableContext(named.ToDisplayString()),
                receiverType: named);

        return new SiteClaim
        {
            Syntax = w,
            FilePath = filePath,
            SpanStart = span,
            ReceiverType = named,
            IsEmpty = isEmpty,
            Assignments = assignments,
            FailCode = null,
            FailReason = null,
        };
    }

    private static SiteClaim FailInitializer(
        WithExpressionSyntax w, string filePath, int span,
        INamedTypeSymbol receiver, string what)
        => SiteClaim.Fail(w, filePath, span,
            MemberDesugarDiagnosticCode.UnsupportedNestedInitializer,
            RecordDiagnostics.UnsupportedNestedInitializer(receiver.ToDisplayString(), what),
            receiverType: receiver);

    private static bool IsMemberOfReceiver(ISymbol member, INamedTypeSymbol receiver)
    {
        var owner = member.ContainingType?.OriginalDefinition;
        for (var t = receiver; t != null; t = t.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, owner)) return true;
        }
        return false;
    }

    /// <summary>站点本地分类 + 类型级认领结论（闭包/树外派生）。</summary>
    internal sealed class SiteClaim
    {
        public required WithExpressionSyntax Syntax { get; init; }
        public required string FilePath { get; init; }
        public required int SpanStart { get; init; }
        public INamedTypeSymbol? ReceiverType { get; init; }
        public required bool IsEmpty { get; init; }
        public required IReadOnlyList<WithAssignment> Assignments { get; init; }
        public MemberDesugarDiagnosticCode? FailCode { get; init; }
        public string? FailReason { get; init; }

        public bool IsLocallyRewritable => FailCode == null;

        public static SiteClaim Fail(
            WithExpressionSyntax w, string filePath, int span,
            MemberDesugarDiagnosticCode code, string reason,
            INamedTypeSymbol? receiverType = null)
            => new()
            {
                Syntax = w,
                FilePath = filePath,
                SpanStart = span,
                ReceiverType = receiverType,
                IsEmpty = false,
                Assignments = Array.Empty<WithAssignment>(),
                FailCode = code,
                FailReason = reason,
            };

        public WithSite ToWithSite()
            => new()
            {
                Syntax = Syntax,
                FilePath = FilePath,
                SpanStart = SpanStart,
                ReceiverType = ReceiverType,
                ReceiverInSource = ReceiverType != null
                    && CloneCtorResolver.IsInSource(ReceiverType),
                IsEmpty = IsEmpty,
                Assignments = Assignments,
                FailCode = FailCode,
                FailReason = FailReason,
            };
    }

    /// <summary>继承链组件：候选 record 经 record 祖先/源内派生闭包连通的全部 record 成员；
    /// Gate 非 null 时（M1 仅位置成员 5220）全组件候选站点不收编。</summary>
    internal sealed class ComponentInfo
    {
        public ComponentInfo(string root) => Root = root;
        public string Root { get; }
        public HashSet<INamedTypeSymbol> Members { get; } =
            new(SymbolEqualityComparer.Default);
        public MemberDesugarDiagnosticCode? GateCode { get; set; }
        public string? GateReason { get; set; }
    }

    /// <summary>整类型级认领结论。<see cref="ComponentRoot"/> 指向继承链组件 id。</summary>
    internal sealed record TypeClaim(
        bool Feasible,
        MemberDesugarDiagnosticCode? FailCode,
        string? FailReason,
        string? ComponentRoot);

    /// <summary>以 INamedTypeSymbol 为元素的并查集（继承链组件定名用）。</summary>
    private sealed class Dsu
    {
        private readonly Dictionary<INamedTypeSymbol, INamedTypeSymbol> _parent =
            new(SymbolEqualityComparer.Default);

        public void Add(INamedTypeSymbol x)
        {
            x = x.OriginalDefinition;
            _parent.TryAdd(x, x);
        }

        public void Union(INamedTypeSymbol a, INamedTypeSymbol b)
        {
            a = a.OriginalDefinition;
            b = b.OriginalDefinition;
            Add(a);
            Add(b);
            var ra = Resolve(a);
            var rb = Resolve(b);
            if (!SymbolEqualityComparer.Default.Equals(ra, rb)) _parent[rb] = ra;
        }

        public string Find(INamedTypeSymbol x)
        {
            x = x.OriginalDefinition;
            Add(x);
            return Resolve(x).ToDisplayString();
        }

        private INamedTypeSymbol Resolve(INamedTypeSymbol x)
        {
            var p = _parent[x];
            if (SymbolEqualityComparer.Default.Equals(p, x)) return x;
            return _parent[x] = Resolve(p);
        }
    }
}
