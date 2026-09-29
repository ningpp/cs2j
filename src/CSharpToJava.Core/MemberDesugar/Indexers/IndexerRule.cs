using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Indexers;

/// <summary>
/// C12 indexer 规则（Kind=Indexer, Order=20；经程序集扫描自动注册，零注册代码）。
/// 三步镜像 PropertyRule：1) 声明收集 + 声明级闸门（partial/extern/static abstract/碰撞）；
/// 2) 站点扫描（读/写/复合/自增/null 条件/初始化器/ref|out/表达式树/dynamic）；
/// 3) IndexerPlan 产出（D17：一个索引器符号一个 plan）。
/// </summary>
[MemberDesugarRule(MemberDesugarKind.Indexer, 20)]
internal sealed class IndexerRule : IMemberDesugarRule
{
    public MemberDesugarKind Kind => MemberDesugarKind.Indexer;
    public int Order => 20;

    public void Analyze(MemberDesugarAnalysisContext context, MemberDesugarPlanBuilder builder)
    {
        var options = context.Options;

        var candidates = new Dictionary<IPropertySymbol,
            (IndexerDeclarationSyntax Decl, SemanticModel Model)>(SymbolEqualityComparer.Default);
        var failures = new Dictionary<IPropertySymbol,
            (IndexerDeclarationSyntax? Decl, MemberDesugarDiagnosticCode Code, string Reason)>(
            SymbolEqualityComparer.Default);
        var userSkipped = new Dictionary<IPropertySymbol, IndexerDeclarationSyntax>(
            SymbolEqualityComparer.Default);
        var allSourceIndexers = new List<IPropertySymbol>();

        // ---- 阶段 1：声明收集 + 声明级闸门 ----
        foreach (var model in context.Models)
        {
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
            foreach (var decl in root.DescendantNodes().OfType<IndexerDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not { IsIndexer: true } prop) continue;
                if (!allSourceIndexers.Contains(prop, SymbolEqualityComparer.Default))
                    allSourceIndexers.Add(prop);
                if (candidates.ContainsKey(prop) || failures.ContainsKey(prop)
                    || userSkipped.ContainsKey(prop)) continue;

                if (MatchesSet(options.SkipQualifiedNames, prop))
                {
                    userSkipped[prop] = decl;
                    continue;
                }

                var gate = DeclarationGate(decl, prop);
                if (gate != null)
                {
                    failures[prop] = (decl, gate.Value.Code, gate.Value.Reason);
                    continue;
                }
                candidates[prop] = (decl, model);
            }
        }

        // ---- 阶段 2：站点扫描 ----
        var siteFiles = new Dictionary<IPropertySymbol, HashSet<string>>(
            SymbolEqualityComparer.Default);
        var siteFailures = new Dictionary<IPropertySymbol,
            (MemberDesugarDiagnosticCode Code, string Reason)>(SymbolEqualityComparer.Default);

        foreach (var model in context.Models)
        {
            var filePath = model.SyntaxTree.FilePath;
            var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

            // dynamic 元素访问保守闸门（D15）：任何 dynamic 接收者的 [] 都可能命中
            // 任意索引器 → 全部候选按 CS2J5136 跳过（对齐 C11 属性 dynamic 处理）。
            foreach (var ea in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
            {
                if (model.GetTypeInfo(ea.Expression).Type is not { TypeKind: TypeKind.Dynamic })
                    continue;
                foreach (var key in candidates.Keys.ToList())
                    MarkFailure(siteFailures, key,
                        MemberDesugarDiagnosticCode.IndexerDynamicSite,
                        IndexerDiagnostics.Dynamic(key.MetadataName));
            }

            foreach (var node in root.DescendantNodes())
            {
                switch (node)
                {
                    case ImplicitElementAccessSyntax iea when iea.Parent is InitializerExpressionSyntax:
                        HandleInitializerElement(iea, model, filePath,
                            candidates, siteFiles, siteFailures);
                        break;
                    case ElementAccessExpressionSyntax ea:
                        HandleElementAccess(ea, model, filePath,
                            candidates, siteFiles, siteFailures, options);
                        break;
                    case ElementBindingExpressionSyntax eb:
                        HandleElementAccess(eb, model, filePath,
                            candidates, siteFiles, siteFailures, options);
                        break;
                }
            }
        }

        // ---- 阶段 3：plan 产出 ----
        foreach (var (prop, declModel) in candidates)
        {
            var (decl, _) = declModel;
            var touched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in prop.Locations.Where(l => l.IsInSource))
                touched.Add(loc.SourceTree!.FilePath);
            if (siteFiles.TryGetValue(prop, out var files))
                foreach (var f in files) touched.Add(f);
            CollectHierarchyLinks(prop, allSourceIndexers, touched);

            if (siteFailures.TryGetValue(prop, out var fail))
            {
                builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.Infeasible,
                    DeclarationFiles(prop), fail.Code, fail.Reason));
                continue;
            }
            builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.Planned,
                touched.ToList(), null, null));
        }

        foreach (var (prop, fail) in failures)
            builder.Add(CreatePlan(prop, fail.Decl, MemberDesugarOutcome.Infeasible,
                DeclarationFiles(prop), fail.Code, fail.Reason));

        foreach (var (prop, decl) in userSkipped)
            builder.Add(CreatePlan(prop, decl, MemberDesugarOutcome.UserSkipped,
                DeclarationFiles(prop), null, null));
    }

    // ---- 声明级闸门（5138 → 5139 → 5140）----

    private static (MemberDesugarDiagnosticCode Code, string Reason)? DeclarationGate(
        IndexerDeclarationSyntax decl, IPropertySymbol prop)
    {
        if (IndexerFacts.IsPartial(decl))
            return (MemberDesugarDiagnosticCode.IndexerPartialDeclaration,
                IndexerDiagnostics.Partial(prop.MetadataName));
        if (prop.IsExtern)
            return (MemberDesugarDiagnosticCode.IndexerUnsupportedDeclaration,
                IndexerDiagnostics.Unsupported(prop.MetadataName, "extern"));
        if (prop.IsStatic && prop.IsAbstract)
            return (MemberDesugarDiagnosticCode.IndexerUnsupportedDeclaration,
                IndexerDiagnostics.Unsupported(prop.MetadataName, "static abstract interface"));
        if (IndexerFacts.HasSignatureCollision(prop))
            return (MemberDesugarDiagnosticCode.IndexerNameCollision,
                IndexerDiagnostics.NameCollision(prop.MetadataName));
        return null;
    }

    // ---- 站点处理 ----

    private static void HandleElementAccess(
        ExpressionSyntax node, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (IndexerDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures,
        MemberDesugarOptions options)
    {
        if (model.GetSymbolInfo(node).Symbol is not IPropertySymbol { IsIndexer: true } p) return;
        if (!p.IsDefinition) p = p.OriginalDefinition;
        if (!candidates.ContainsKey(p)) return;
        Record(siteFiles, p, filePath);

        // 表达式树（CS2J5135，force 可解除）
        if (MemberDesugarSyntaxFacts.IsInsideExpressionTree(node, model)
            && !MatchesSet(options.ForceQualifiedNames, p))
        {
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerExpressionTreeSite,
                IndexerDiagnostics.ExpressionTree(p.MetadataName));
            return;
        }

        // 左值分类
        if (node.Parent is AssignmentExpressionSyntax assign && ReferenceEquals(assign.Left, node))
        {
            if (assign.IsKind(SyntaxKind.SimpleAssignmentExpression))
            {
                // 消费位写（x = (c[i] = v)）：值索引器不可提升 → 5137；
                // 初始化器位由 ImplicitElementAccess 通道处理；ref 索引器走 getter 变量。
                if (assign.Parent is not ExpressionStatementSyntax
                    && assign.Parent is not InitializerExpressionSyntax
                    && !IndexerFacts.ReturnsByRef(p))
                    MarkFailure(siteFailures, p,
                        MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                        IndexerDiagnostics.NonLiftable(p.MetadataName));
            }
            else
            {
                // 复合：仅语句位处理（含实参提升）；表达式位 → 5137（ref 索引器除外）。
                if (assign.Parent is not ExpressionStatementSyntax
                    && !IndexerFacts.ReturnsByRef(p))
                    MarkFailure(siteFailures, p,
                        MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                        IndexerDiagnostics.NonLiftable(p.MetadataName));
            }
            return;
        }

        // ++/--：仅语句位处理（结果未被消费）；消费位值索引器 → 5137。
        var isIncrementTarget =
            node.Parent is PrefixUnaryExpressionSyntax pre
                && (pre.IsKind(SyntaxKind.PreIncrementExpression)
                    || pre.IsKind(SyntaxKind.PreDecrementExpression))
            || node.Parent is PostfixUnaryExpressionSyntax post
                && (post.IsKind(SyntaxKind.PostIncrementExpression)
                    || post.IsKind(SyntaxKind.PostDecrementExpression));
        if (isIncrementTarget && node.Parent!.Parent is not ExpressionStatementSyntax
            && !IndexerFacts.ReturnsByRef(p))
        {
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                IndexerDiagnostics.NonLiftable(p.MetadataName));
        }
        // 其余（读 / ref|out 实参 / ref var）无需失败标记。
    }

    private static void HandleInitializerElement(
        ImplicitElementAccessSyntax iea, SemanticModel model, string filePath,
        Dictionary<IPropertySymbol, (IndexerDeclarationSyntax Decl, SemanticModel Model)> candidates,
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles,
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> siteFailures)
    {
        if (model.GetSymbolInfo(iea).Symbol is not IPropertySymbol { IsIndexer: true } p) return;
        if (!p.IsDefinition) p = p.OriginalDefinition;
        if (!candidates.ContainsKey(p)) return;
        Record(siteFiles, p, filePath);

        var enclosing = iea.Ancestors().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
        if (enclosing == null || !IndexerFacts.IsLiftableCreation(enclosing))
            MarkFailure(siteFailures, p,
                MemberDesugarDiagnosticCode.IndexerNonLiftableSite,
                IndexerDiagnostics.NonLiftable(p.MetadataName));
    }

    // ---- 层次联动（override 链 / 接口实现，镜像 C11 CollectHierarchyLinks）----

    private static void CollectHierarchyLinks(
        IPropertySymbol prop, IReadOnlyList<IPropertySymbol> all, SortedSet<string> files)
    {
        foreach (var other in all)
        {
            if (SymbolEqualityComparer.Default.Equals(other, prop)) continue;
            if (InOverrideChain(other, prop) || InOverrideChain(prop, other)
                || other.ExplicitInterfaceImplementations.Contains(prop, SymbolEqualityComparer.Default)
                || ImplementsImplicit(other, prop))
            {
                foreach (var loc in other.Locations.Where(l => l.IsInSource))
                    files.Add(loc.SourceTree!.FilePath);
            }
        }
    }

    private static bool InOverrideChain(IPropertySymbol derived, IPropertySymbol potentialBase)
    {
        for (var b = derived.OverriddenProperty; b != null; b = b.OverriddenProperty)
            if (SymbolEqualityComparer.Default.Equals(b, potentialBase)) return true;
        return false;
    }

    private static bool ImplementsImplicit(IPropertySymbol impl, IPropertySymbol ifaceProp)
        => ifaceProp.ContainingType is { TypeKind: TypeKind.Interface }
           && impl.ContainingType != null
           && SymbolEqualityComparer.Default.Equals(
               impl.ContainingType.FindImplementationForInterfaceMember(ifaceProp), impl);

    // ---- 小工具 ----

    private static IndexerPlan CreatePlan(
        IPropertySymbol prop, IndexerDeclarationSyntax? decl, MemberDesugarOutcome outcome,
        IReadOnlyList<string> touchedFiles, MemberDesugarDiagnosticCode? code, string? reason)
        => new()
        {
            Id = IndexerPlan.BuildId(prop),
            Kind = MemberDesugarKind.Indexer,
            Symbol = prop,
            Outcome = outcome,
            TouchedFiles = touchedFiles,
            FailCode = code,
            FailReason = reason,
            Indexer = prop,
            MetadataName = prop.MetadataName,
            GetterMethodName = AccessorsToMethods.GetterName(prop.MetadataName),
            SetterMethodName = AccessorsToMethods.SetterName(prop.MetadataName),
            HasSetter = prop.SetMethod != null,
            HasInitAccessor = decl != null && IndexerFacts.HasInitAccessor(decl),
        };

    private static void Record(
        Dictionary<IPropertySymbol, HashSet<string>> siteFiles, IPropertySymbol prop, string filePath)
    {
        if (!siteFiles.TryGetValue(prop, out var set))
            siteFiles[prop] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        set.Add(filePath);
    }

    private static void MarkFailure(
        Dictionary<IPropertySymbol, (MemberDesugarDiagnosticCode Code, string Reason)> failures,
        IPropertySymbol prop, MemberDesugarDiagnosticCode code, string reason)
        => failures.TryAdd(prop, (code, reason));

    private static bool MatchesSet(IReadOnlySet<string> names, IPropertySymbol prop)
    {
        var type = prop.ContainingType?.ToDisplayString();
        return names.Contains($"{type}::{prop.MetadataName}")
               || (type != null && names.Contains(type));
    }

    private static IReadOnlyList<string> DeclarationFiles(IPropertySymbol prop)
        => prop.Locations.Where(l => l.IsInSource)
            .Select(l => l.SourceTree!.FilePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
