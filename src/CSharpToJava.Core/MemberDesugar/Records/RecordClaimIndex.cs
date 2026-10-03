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

    // M2 分步发射闸门（随提交翻开，最终随 WR-5 一并删除）：分析/物料/认领计算全部先行，
    // 但 reshape 终形发射（RC-3 class / RC-4 struct）与嵌套 with（WR-5）落地前，运行期
    // 维持 M1 站点跳过行为，保证每个中间提交 Enabled=true 输出也合法且与 M1 一致。
    //   PositionalClassReshapeEnabled：RC-3（位置 record class reshape）
    //   PositionalStructReshapeEnabled：RC-4（位置 record struct / 普通 struct 值拷贝）
    //   NestedWithEnabled：WR-5（嵌套 with 后序重写）
    internal const bool PositionalClassReshapeEnabled = false;
    internal const bool PositionalStructReshapeEnabled = false;
    internal const bool NestedWithEnabled = false;

    /// <summary>该类型的 M2 新路径发射是否已随提交就位（RecordRule 临时闸门 / 5123 认领共用）。</summary>
    internal static bool IsEmissionReady(INamedTypeSymbol type, PositionalShape? shape)
    {
        if (shape != null)
        {
            return shape.IsStruct ? PositionalStructReshapeEnabled : PositionalClassReshapeEnabled;
        }
        // 非位置 record struct / 普通 struct 的 with 值拷贝（WR-4）随 struct reshape 同批。
        return type.TypeKind != TypeKind.Struct || PositionalStructReshapeEnabled;
    }

    private readonly Dictionary<string, SiteClaim> _sitesByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<INamedTypeSymbol, TypeClaim> _typeClaims =
        new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, ComponentInfo> _components =
        new(StringComparer.Ordinal);
    // M2：通过 5220 闸门、本 run 实际要 reshape 的位置 record 物料（键归 OriginalDefinition）。
    private readonly Dictionary<INamedTypeSymbol, PositionalShape> _shapes =
        new(SymbolEqualityComparer.Default);
    // M2 临时（WR-5 翻开前）：含嵌套 with 的接收者类型集合——plan 维持 5224 站点跳过、
    // 所属组件不发工厂（M1 行为）。NestedWithEnabled 翻开后恒空。
    private readonly HashSet<INamedTypeSymbol> _nestedPendingTypes =
        new(SymbolEqualityComparer.Default);

    /// <summary>临时闸门：含嵌套 with、WR-5 落地前整 plan 跳过且不发工厂的类型。</summary>
    public IReadOnlyCollection<INamedTypeSymbol> PendingNestedTypes => _nestedPendingTypes;

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

        // M2 临时（WR-5 翻开后本段恒空）：嵌套 with 双方接收者类型登记，供 RecordRule
        // 维持 M1 的 5224 整 plan 跳过 + 组件不发工厂。
        if (!NestedWithEnabled)
        {
            foreach (var site in rawSites)
            {
                if (site.FailCode != null || site.ReceiverType is not { } nestedReceiver) continue;
                var isNested = site.Syntax.Ancestors().OfType<WithExpressionSyntax>().Any()
                    || site.Syntax.DescendantNodes().OfType<WithExpressionSyntax>().Any();
                if (isNested)
                    index._nestedPendingTypes.Add(nestedReceiver.OriginalDefinition);
            }
        }

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

        // 候选（M2）：源内 record class（含位置）/ record struct / 普通 struct，且至少一个
        // 站点本地分类通过。struct 走 WR-4 值拷贝，无工厂、无继承链闭包（值类型隐式密封）。
        var candidates = sitesByType
            .Where(kv => kv.Value.Any(s => s.IsLocallyRewritable)
                && IsEligibleReceiverKind(kv.Key))
            .Select(kv => kv.Key)
            .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        var classCandidates = candidates
            .Where(CloneCtorResolver.IsRecordClass)
            .ToHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        // ---- 3b. 位置候选 reshape 物料 + 5220 闸门（仅当存在非空可行站点才需要 reshape；
        // 纯空站点走原工厂/值拷贝即可，声明不动）。----
        var shapeGates = new Dictionary<INamedTypeSymbol, ShapeGate>(
            SymbolEqualityComparer.Default);
        foreach (var type in candidates.Where(CloneCtorResolver.IsPositional))
        {
            var hasNonEmptySite = sitesByType[type].Any(s => s.IsLocallyRewritable && !s.IsEmpty);
            if (!hasNonEmptySite) continue;
            var (shape, gate) = PositionalRecordShapeAnalyzer.Analyze(type, compilation);
            if (shape != null) index._shapes[type] = shape;
            else if (gate != null) shapeGates[type] = gate;
        }

        // ---- 4. 继承链闭包（仅 record class 候选 + record 祖先 + 源内 record 派生）+ 并查集 ----
        // 组件全部以 OriginalDefinition 为键（构造泛型归并到定义形）。
        var dsu = new Dsu();
        foreach (var candidate in classCandidates)
        {
            dsu.Add(candidate);
            foreach (var a in CloneCtorResolver.RecordAncestors(candidate))
                dsu.Union(candidate, a.OriginalDefinition);
            foreach (var d in CloneCtorResolver.SourceRecordDescendants(candidate, sourceRecords))
                dsu.Union(candidate, d.OriginalDefinition);
        }

        // ---- 5. 组件成员聚合 + 组件级闸门：候选成员 reshape 失败 → 全组件 5220（决策 8c
        // 计划原子；非候选的位置闭包成员不 reshape，保留其合成 ctor/复制 ctor，不挡组件）----
        var components = new Dictionary<string, ComponentInfo>(StringComparer.Ordinal);
        foreach (var candidate in classCandidates)
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
        foreach (var (gated, gate) in shapeGates)
        {
            if (!CloneCtorResolver.IsRecordClass(gated)) continue;
            var root = dsu.Find(gated);
            if (components.TryGetValue(root, out var component))
            {
                component.GateCode = gate.Code;
                component.GateReason = gate.Reason;
            }
        }
        foreach (var component in components.Values)
            index._components[component.Root] = component;

        // ---- 6. 逐候选类型定案 ----
        foreach (var type in candidates)
        {
            var ownSites = sitesByType[type];

            var bad = ownSites.FirstOrDefault(s => !s.IsLocallyRewritable);
            if (bad != null)
            {
                index._typeClaims[type] = new TypeClaim(
                    false, bad.FailCode!.Value, bad.FailReason!,
                    CloneCtorResolver.IsRecordClass(type) ? dsu.Find(type) : null);
                continue;
            }

            // struct/record struct：值类型无虚工厂/树外派生/复制 ctor 检查，
            // 仅有位置 record struct 的 reshape 闸门。
            if (!CloneCtorResolver.IsRecordClass(type))
            {
                if (shapeGates.TryGetValue(type, out var structGate))
                {
                    index._typeClaims[type] = new TypeClaim(
                        false, structGate.Code, structGate.Reason, null);
                    continue;
                }
                index._typeClaims[type] = new TypeClaim(true, null, null, null);
                continue;
            }

            var componentRoot = dsu.Find(type);
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

    private static bool IsEligibleReceiverKind(INamedTypeSymbol type)
        => CloneCtorResolver.IsRecordClass(type) || type.TypeKind == TypeKind.Struct;

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
        if (prop.SetMethod == null) return false;
        // 位置属性仅当其类型有可行 reshape 物料（set_X 终形会发射）时才让位；
        // 未被 reshape 的位置属性仍走 5123/5124 原路径。
        if (PropertyFacts.IsPositionalRecordProperty(prop))
            return IsPositionalPropertyClaimed(prop);
        return true;
    }

    /// <summary>5123 让位钩（M2）：该位置属性是否属于本 run 可行 reshape 的位置 record
    /// （终形会发字段+get_X/set_X，PropertyRule 据此产 Planned PropertyPlan 而非 5123）。</summary>
    public bool IsPositionalPropertyClaimed(IPropertySymbol prop)
    {
        var type = prop.ContainingType?.OriginalDefinition;
        if (type == null || !_shapes.TryGetValue(type, out var shape)) return false;
        // 原子性：类型认领不可行（任意站点命中闸门）时声明不 reshape，位置属性不认领。
        if (!_typeClaims.TryGetValue(type, out var claim) || !claim.Feasible) return false;
        // reshape 终形未发射的中间提交不认领（PropertyRule 维持 5123，输出与 M1 一致）。
        if (!IsEmissionReady(type, shape)) return false;
        var target = prop.OriginalDefinition;
        return shape.Members.Any(m => SymbolEqualityComparer.Default.Equals(
            m.Property.OriginalDefinition, target));
    }

    /// <summary>取可行 reshape 物料（RecordRule 挂 plan / PropertyRule 取契约字段名）。</summary>
    public bool TryGetShape(INamedTypeSymbol type, out PositionalShape shape)
        => _shapes.TryGetValue(type.OriginalDefinition, out shape!);

    /// <summary>位置属性 → reshape 终形后备字段名（与发射体逐字同名）。</summary>
    public bool TryGetPositionalFieldName(IPropertySymbol prop, out string fieldName)
    {
        fieldName = "";
        var type = prop.ContainingType?.OriginalDefinition;
        if (type == null || !_shapes.TryGetValue(type, out var shape)) return false;
        var member = shape.Members.FirstOrDefault(m => SymbolEqualityComparer.Default.Equals(
            m.Property.OriginalDefinition, prop.OriginalDefinition));
        if (member == null) return false;
        fieldName = member.FieldName;
        return true;
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

        // 2) 嵌套 with（WR-5，M2 放开）：内层作为外层 RHS/接收者，由 plan 期后序 +
        // 重写期语句级提升统一处理；此处不再失败。初始化器内「裸对象/集合初始化器」
        // （X = { ... }）仍在逐成员扫描处 5224。
        var nestedInWith = w.Ancestors().OfType<WithExpressionSyntax>().Any();

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

        // 4) 匿名类型。
        if (named.IsAnonymousType)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.AnonymousTypeWith,
                RecordDiagnostics.AnonymousTypeWith(), receiverType: named);

        // 5) 接收者形态分流（M2）：tuple/record struct/普通 struct 走 WR-4 值拷贝；
        // 位置 record class 走 RC-3 reshape（可行性由整类型 shape 闸门在 plan 期定案，
        // 站点本地分类不再提前 5220）。
        var isValueCopy = false;
        if (named.IsTupleType)
        {
            isValueCopy = true;
        }
        else if (named.IsRecord)
        {
            if (!CloneCtorResolver.IsInSource(named))
                return SiteClaim.Fail(w, filePath, span,
                    MemberDesugarDiagnosticCode.MetadataRecordReceiver,
                    RecordDiagnostics.MetadataRecordReceiver(named.ToDisplayString()),
                    receiverType: named);
            if (CloneCtorResolver.IsRecordStruct(named))
                isValueCopy = true;
        }
        else if (named.TypeKind == TypeKind.Struct)
        {
            if (!CloneCtorResolver.IsInSource(named))
                return SiteClaim.Fail(w, filePath, span,
                    MemberDesugarDiagnosticCode.NonRecordClassWith,
                    RecordDiagnostics.NonRecordClassWith(named.ToDisplayString(),
                        "the struct receiver is declared in referenced metadata; no set_X "
                        + "accessor methods can be assumed there"),
                    receiverType: named);
            isValueCopy = true;
        }
        else
        {
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.NonRecordClassWith,
                RecordDiagnostics.NonRecordClassWith(named.ToDisplayString(),
                    "only record class/struct receivers are supported (CS8858 for plain classes)"),
                receiverType: named);
        }

        // readonly 值类型非空 with：克隆局部的字段/属性仍只读（CS8340/CS8341），
        // set_X/字段直写无合法发射面 → 5220；空 with 纯值拷贝不受限。
        var hasInitializer = w.Initializer is { Expressions.Count: > 0 };
        if (isValueCopy && !named.IsTupleType && named.IsReadOnly && hasInitializer)
            return SiteClaim.Fail(w, filePath, span,
                MemberDesugarDiagnosticCode.PositionalReshapeGate,
                RecordDiagnostics.ReadonlyValueCopyBlocked(named.ToDisplayString()),
                receiverType: named);

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
                    // 位置属性（init 访问器亦有 SetMethod）：reshape 终形放宽为 set_X，
                    // 可行性由整类型 shape 闸门定案（决策 6/8c），站点扫描不再拦。
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

        // 7) 语句提升闸门（决策 10 + WR-4）：非空 record class 站点、以及所有值拷贝站点
        // （含空初始化器——struct 空 with 也保守发一次 wsrc/wclone）必须可提升；嵌套内层
        // with 由外层提升接管（外层不可提升时外层自身失败，原子跳过整条语句）。
        var needsLift = !isEmpty || isValueCopy;
        if (needsLift && !nestedInWith && !CloneCtorResolver.IsLiftableSite(w))
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
            IsValueCopy = isValueCopy,
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
        public bool IsValueCopy { get; init; }
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
                IsValueCopy = IsValueCopy,
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
