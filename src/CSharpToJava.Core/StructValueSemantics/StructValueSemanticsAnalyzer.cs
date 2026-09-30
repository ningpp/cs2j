using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// Phase 1/2 整树分析产物：残余 struct 集合、装箱可达集合（按 compilation 缓存）、
/// 变异方法集、全站点点表与 ClonePlan 集合。
/// </summary>
internal sealed class StructValueSemanticsAnalysis
{
    public required Compilation Compilation { get; init; }
    public required StructValueSemanticsOptions Options { get; init; }
    public required StructValueSemanticsPlanSet PlanSet { get; init; }

    /// <summary>残余 struct 原始定义符号 → 其 plan（含 Skipped/Infeasible）。</summary>
    public Dictionary<INamedTypeSymbol, ClonePlan> PlansBySymbol { get; }
        = new(SymbolEqualityComparer.Default);

    /// <summary>装箱可达 struct 原始定义集合（BoxingReachabilityAnalyzer 接线）。</summary>
    public HashSet<INamedTypeSymbol> BoxingReachableStructs { get; }
        = new(SymbolEqualityComparer.Default);

    /// <summary>计划期诊断（SV001/SV010/SV011/SV012/SV020/SV050/SV051 …）。</summary>
    public List<StructValueSemanticsDiagnostic> PlanningDiagnostics { get; } = new();

    /// <summary>计划级聚合计数（文件/站点级计数在 RewriteFile 期累加）。</summary>
    public StructValueSemanticsStatistics PlanningStatistics { get; } = new();

    /// <summary>被排除（SV031 回退）的 plan id，PlanningDiagnostics 输出时过滤。</summary>
    public required IReadOnlySet<string> ExcludedPlanIds { get; init; }
}

/// <summary>
/// Phase 1（残余集合/变异方法集/站点全枚举）+ Phase 2（ClonePlan/Shim 决策）。
/// 判定全部经 SemanticModel，禁止纯文本猜别名（硬不变式）。
/// </summary>
internal static partial class StructValueSemanticsAnalyzer
{
    public static StructValueSemanticsAnalysis AnalyzeAndPlan(
        Compilation compilation,
        StructValueSemanticsOptions options,
        IReadOnlySet<string> excludedPlanIds)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(options);

        var planSet = new StructValueSemanticsPlanSet();
        var analysis = new StructValueSemanticsAnalysis
        {
            Compilation = compilation,
            Options = options,
            PlanSet = planSet,
            ExcludedPlanIds = excludedPlanIds,
        };

        BuildPlans(analysis, excludedPlanIds);
        return analysis;
    }

    /// <summary>
    /// Phase 1+2：枚举残余 struct、解析 shim 现状、计算变异方法集与全部值流站点，
    /// 产出确定性排序的 ClonePlan 集合。
    /// </summary>
    private static void BuildPlans(
        StructValueSemanticsAnalysis analysis,
        IReadOnlySet<string> excludedPlanIds)
    {
        var compilation = analysis.Compilation;
        var options = analysis.Options;

        // 装箱可达：同程序集直接复用 E13 分析器，按 compilation 缓存。
        var boxing = GetBoxingReachableStructs(compilation);
        analysis.BoxingReachableStructs.UnionWith(boxing);

        // === Phase 1a：残余 struct 集合（源内声明、非 ref、声明处无 readonly）===
        var sourceTypes = new List<INamedTypeSymbol>();
        CollectTypes(compilation.GlobalNamespace, sourceTypes);

        foreach (var type in sourceTypes)
        {
            if (!Transformers.StructCloneHelper.IsUserDefinedStruct(type)) continue;

            analysis.PlanningStatistics.StructsScanned++;

            var definition = type.OriginalDefinition;
            if (analysis.PlansBySymbol.ContainsKey(definition)) continue;

            var qn = definition.ToDisplayString();
            var parts = definition.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .ToList();
            if (parts.Count == 0) continue;

            if (definition.IsRefLikeType)
            {
                AddPlanningInfo(analysis, StructValueSemanticsDiagnosticCode.SV011,
                    "ref struct excluded from struct-value-semantics (handled by C01/C06).",
                    definition, excludedPlanIds);
                continue;
            }

            // readonly 关键字必须出现在所有 partial 部件；任一部件有即整体 readonly。
            if (parts.Any(p => p.Modifiers.Any(m =>
                    m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ReadOnlyKeyword))))
            {
                AddPlanningInfo(analysis, StructValueSemanticsDiagnosticCode.SV012,
                    "readonly struct does not participate in struct-value-semantics.",
                    definition, excludedPlanIds);
                continue;
            }

            var plan = new ClonePlan
            {
                Id = "SV:" + qn,
                StructShortName = definition.Name,
                StructQualifiedName = qn,
                Symbol = definition,
                IsForced = options.ForceQualifiedNames.Contains(qn),
            };

            var primaryRef = definition.DeclaringSyntaxReferences
                .FirstOrDefault(r => r.GetSyntax()
                    is Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax);
            plan.PrimaryDeclarationFile = primaryRef?.SyntaxTree.FilePath;

            if (options.SkipQualifiedNames.Contains(qn))
            {
                plan.Outcome = ClonePlanOutcome.Skipped;
                plan.FailCode = StructValueSemanticsDiagnosticCode.SV020;
                plan.FailReason = "struct explicitly excluded via overrides.skip; value-copy " +
                    "boundaries are left to downstream handling.";
                plan.FailLocation = definition.Locations.FirstOrDefault(l => l.IsInSource);
                analysis.PlanningStatistics.PlansSkipped++;
                analysis.PlanningStatistics.SkippedByReason.Increment(
                    StructValueSemanticsDiagnosticCode.SV020);
            }

            analysis.PlansBySymbol[definition] = plan;
            analysis.PlanSet.Plans.Add(plan);
        }

        // 确定性排序（符号完全限定名），站点枚举/回退顺序可复现。
        analysis.PlanSet.Plans.Sort((a, b) =>
            string.CompareOrdinal(a.StructQualifiedName, b.StructQualifiedName));

        // === Phase 1b：变异实例方法集（复用 StructAnalyzer，禁止复制判定逻辑）===
        foreach (var plan in analysis.PlanSet.Plans)
        {
            if (!plan.IsActive) continue;
            ComputeMutatingMembers(analysis, plan);
        }

        // === Phase 1b'：SV040/SV041/SV042 语言无解构造 → plan 期 Infeasible（Error）===
        foreach (var plan in analysis.PlanSet.Plans)
        {
            if (!plan.IsActive) continue;
            if (StructValueSemanticsEscapeAnalyzer.Detect(plan, compilation))
            {
                analysis.PlanningStatistics.PlansSkipped++;
                analysis.PlanningStatistics.SkippedByReason.Increment(plan.FailCode!.Value);
            }
        }

        // === Phase 2a：shim 决策与合成（字段递归 Clone 依赖全部 plan 终态前的激活集）===
        foreach (var plan in analysis.PlanSet.Plans)
        {
            if (!plan.IsActive) continue;
            CloneShimGenerator.ResolveAndGenerate(plan, analysis.PlansBySymbol);
            if (!plan.IsActive)
            {
                analysis.PlanningStatistics.PlansSkipped++;
                analysis.PlanningStatistics.SkippedByReason.Increment(
                    plan.FailCode ?? StructValueSemanticsDiagnosticCode.SV020);
            }
        }

        // === Phase 1c：值流站点全枚举（VS-1/2/4；VS-3/5 在 M4 补）===
        var enumerator = new StructValueSemanticsSiteEnumerator(analysis);
        enumerator.Enumerate();

        // === Phase 2b：触碰面归集 + 计划期诊断/统计 ===
        foreach (var plan in analysis.PlanSet.Plans)
        {
            if (excludedPlanIds.Contains(plan.Id)) continue;
            if (plan.PrimaryDeclarationFile != null)
                plan.TouchedFiles.Add(plan.PrimaryDeclarationFile);
            foreach (var site in plan.Sites)
            {
                plan.TouchedFiles.Add(site.FilePath);
                plan.Touches.Add(site.Touch);
            }

            switch (plan.Outcome)
            {
                case ClonePlanOutcome.Active when plan.ShimKind == ShimKind.ReuseExistingClone:
                    analysis.PlanningStatistics.StructsReusedClone++;
                    AddPlanningInfo(analysis, StructValueSemanticsDiagnosticCode.SV010,
                        $"existing public parameterless Clone() reused; {plan.SiteCount} value-copy site(s) planned.",
                        plan.Symbol, excludedPlanIds);
                    analysis.PlanningStatistics.StructsPlanned++;
                    break;
                case ClonePlanOutcome.Active:
                    analysis.PlanningStatistics.StructsPlanned++;
                    if (plan.GeneratedCloneMethod != null)
                        analysis.PlanningStatistics.ShimsGenerated++;
                    AddPlanningInfo(analysis, StructValueSemanticsDiagnosticCode.SV001,
                        $"struct included in plan ({(plan.GeneratedCopyConstructor != null
                            ? "copy ctor + Clone() shim"
                            : "Clone() shim, copy ctor already present")}); " +
                        $"{plan.SiteCount} value-copy site planned.",
                        plan.Symbol, excludedPlanIds);
                    break;
                case ClonePlanOutcome.Skipped:
                    AddPlanningDiagnostic(analysis, StructValueSemanticsSeverity.Warning,
                        plan.FailCode ?? StructValueSemanticsDiagnosticCode.SV020,
                        plan.FailReason ?? "plan skipped.", plan.Symbol,
                        plan.FailLocation, excludedPlanIds);
                    break;
                default:
                    // SV040/SV041/SV042 为语言无解 Error；其余 Infeasible（SV050/SV051）为 Warning。
                    var infeasibleCode = plan.FailCode ?? StructValueSemanticsDiagnosticCode.SV050;
                    var severity = infeasibleCode is StructValueSemanticsDiagnosticCode.SV040
                        or StructValueSemanticsDiagnosticCode.SV041
                        or StructValueSemanticsDiagnosticCode.SV042
                        ? StructValueSemanticsSeverity.Error
                        : StructValueSemanticsSeverity.Warning;
                    AddPlanningDiagnostic(analysis, severity, infeasibleCode,
                        plan.FailReason ?? "plan infeasible.", plan.Symbol,
                        plan.FailLocation, excludedPlanIds);
                    break;
            }
        }
    }

    /// <summary>
    /// 变异实例方法集：直接变异（StructAnalyzer.IsMutating）+ 委托传递闭包
    /// （IsTransitiveMigrationCandidate + CallsMutatingMethod，与 E13 同范式）；
    /// partial 方法按各自声明树取 SemanticModel。另集非 init 实例属性 setter。
    /// </summary>
    private static void ComputeMutatingMembers(
        StructValueSemanticsAnalysis analysis, ClonePlan plan)
    {
        var compilation = analysis.Compilation;
        var analyzersByTree = new Dictionary<Microsoft.CodeAnalysis.SyntaxTree,
            ReadOnlyStructMaker.StructAnalyzer>();

        ReadOnlyStructMaker.StructAnalyzer AnalyzerFor(Microsoft.CodeAnalysis.SyntaxTree tree)
        {
            if (!analyzersByTree.TryGetValue(tree, out var analyzer))
            {
                analyzer = new ReadOnlyStructMaker.StructAnalyzer(
                    new ReadOnlyStructMaker.ReadOnlyStructMakerOptions(),
                    compilation.GetSemanticModel(tree),
                    analysis.BoxingReachableStructs);
                analyzersByTree[tree] = analyzer;
            }
            return analyzer;
        }

        var methods = plan.Symbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsStatic)
            .Select(m => (
                Method: m,
                Decl: (Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax?)
                    m.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax())
                        .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
                        .FirstOrDefault(d => d.Body != null || d.ExpressionBody != null)))
            .Where(x => x.Decl != null)
            .ToList();

        foreach (var (method, decl) in methods)
        {
            if (AnalyzerFor(decl!.SyntaxTree).IsMutating(decl))
                plan.MutatingMethods.Add(method);
        }

        // 传递闭包：void/return-this 委托方法调到任一当前变异方法即纳入（与 E13 同范式）。
        var changed = true;
        while (changed)
        {
            changed = false;
            var mutatingNames = plan.MutatingMethods.Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var (method, decl) in methods)
            {
                if (plan.MutatingMethods.Contains(method)) continue;
                if (!ReadOnlyStructMaker.StructAnalyzer
                        .IsTransitiveMigrationCandidate(method, decl!))
                {
                    continue;
                }

                if (AnalyzerFor(decl!.SyntaxTree)
                        .CallsMutatingMethod(decl, mutatingNames, method))
                {
                    plan.MutatingMethods.Add(method);
                    changed = true;
                }
            }
        }

        foreach (var property in plan.Symbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.IsIndexer) continue;
            if (property.SetMethod is { IsInitOnly: false })
                plan.MutableProperties.Add(property);
        }
    }

    private static void CollectTypes(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> acc)
    {
        foreach (var type in namespaceSymbol.GetTypeMembers())
        {
            acc.Add(type);
            CollectNested(type, acc);
        }

        foreach (var nested in namespaceSymbol.GetNamespaceMembers())
            CollectTypes(nested, acc);
    }

    private static void CollectNested(INamedTypeSymbol type, List<INamedTypeSymbol> acc)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            acc.Add(nested);
            CollectNested(nested, acc);
        }
    }

    // 装箱可达按 compilation 缓存（仿 ReadOnlyStructMaker 的实例缓存；用 CWT 防止测试场景泄漏）。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Compilation, HashSet<INamedTypeSymbol>> BoxingCache = new();

    private static HashSet<INamedTypeSymbol> GetBoxingReachableStructs(Compilation compilation)
    {
        if (BoxingCache.TryGetValue(compilation, out var cached)) return cached;
        cached = ReadOnlyStructMaker.BoxingReachabilityAnalyzer
            .ComputeBoxingReachableStructs(compilation);
        BoxingCache.AddOrUpdate(compilation, cached);
        return cached;
    }

    private static void AddPlanningInfo(
        StructValueSemanticsAnalysis analysis,
        StructValueSemanticsDiagnosticCode code,
        string message,
        INamedTypeSymbol symbol,
        IReadOnlySet<string> excludedPlanIds,
        Location? location = null)
        => AddPlanningDiagnostic(analysis, StructValueSemanticsSeverity.Info,
            code, message, symbol, location, excludedPlanIds);

    private static void AddPlanningDiagnostic(
        StructValueSemanticsAnalysis analysis,
        StructValueSemanticsSeverity severity,
        StructValueSemanticsDiagnosticCode code,
        string message,
        INamedTypeSymbol symbol,
        Location? location,
        IReadOnlySet<string> excludedPlanIds)
    {
        if (excludedPlanIds.Contains("SV:" + symbol.ToDisplayString())) return;
        var loc = location ?? symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = loc?.GetLineSpan().StartLinePosition;
        analysis.PlanningDiagnostics.Add(new StructValueSemanticsDiagnostic(
            severity, code, message,
            loc?.SourceTree?.FilePath,
            (span?.Line ?? 0) + 1, (span?.Character ?? 0) + 1,
            symbol.ToDisplayString()));
    }
}

internal static class StructValueSemanticsDictionaryExtensions
{
    public static void Increment<T>(this Dictionary<T, int> dictionary, T key) where T : notnull
    {
        dictionary.TryGetValue(key, out var value);
        dictionary[key] = value + 1;
    }
}
