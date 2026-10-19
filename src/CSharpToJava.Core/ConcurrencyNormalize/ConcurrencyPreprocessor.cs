using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ConcurrencyNormalize;

/// <summary>
/// C29 lock/Monitor 并发原语归一预处理器公共入口（设计 §4.1/§4.2）。
/// runner 建完全树编译后 <see cref="BindCompilation"/> + <see cref="Analyze"/> 一次成型
/// 方法级 region plan；逐文件 <see cref="RewriteFile"/>（Changed=false 返回 null，runner
/// 字节级复制）；改写后 <see cref="ResidualScan"/> 与 active/skipped plan 对账。
/// 计划级回退由 CLI 驱动经 <see cref="ExcludePlans"/> 后从原始副本重放。
/// </summary>
public sealed class ConcurrencyPreprocessor
{
    private readonly ConcurrencyOptions _options;
    private CSharpCompilation? _compilation;
    private ConcurrencyAnalysis? _analysis;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);
    private bool _analysisCountersMerged;

    /// <summary>性能：文件 → 该文件触碰的 plan 列表。索引只建一次，
    /// 替代逐文件全量 plans.Where(p => p.TouchedFiles.Contains(filePath)) 扫描
    /// （画像 2026-10-18：933 文件 × plans 线性扫描）。</summary>
    private Dictionary<string, List<ConcurrencyRegionPlan>>? _plansByFile;

    /// <summary>性能：文件 → 该文件的诊断列表。替代逐文件全量 diagnostics.Where 扫描。</summary>
    private Dictionary<string, List<ConcurrencyDiagnostic>>? _diagnosticsByFile;

    /// <summary>每线程复用一个 AdhocWorkspace：格式化服务的宿主由 workspace 提供，
    /// 复用可省掉逐文件新建 workspace 的开销。
    /// 线程内独占使用，故无需考虑跨线程竞争。</summary>
    [ThreadStatic]
    private static AdhocWorkspace? _threadWorkspace;

    /// <summary>性能：复用同一 parse options 实例，避免每次创建新的 CSharpParseOptions。</summary>
    [ThreadStatic]
    private static CSharpParseOptions? _threadParseOptions;

    private static AdhocWorkspace SharedWorkspace() => _threadWorkspace ??= new AdhocWorkspace();

    private static CSharpParseOptions SharedParseOptions()
        => _threadParseOptions ??= new CSharpParseOptions(LanguageVersion.Latest);

    public ConcurrencyPreprocessor(ConcurrencyOptions? options = null)
    {
        _options = options ?? new ConcurrencyOptions();
    }

    /// <summary>整树编译就绪后绑定（Data flow §4.2 第 1 步；基线诊断快照由驱动做）。</summary>
    public void BindCompilation(CSharpCompilation compilation)
    {
        _compilation = compilation;
        _analysis = null;
        _analysisCountersMerged = false;
        _plansByFile = null;
        _diagnosticsByFile = null;
    }

    /// <summary>整树一次分析，plan 一次成型。幂等：已分析则不再重跑。</summary>
    public void Analyze()
    {
        if (_analysis != null) return;
        var compilation = _compilation
            ?? throw new InvalidOperationException(
                "Preprocessor has not bound a compilation; call BindCompilation first.");
        _analysis = new MonitorRewriteAnalyzer(_options).Analyze(compilation);
        BuildFileIndexes();
    }

    /// <summary>构建文件级索引，供逐文件 RewriteFile 做 O(1) 查询
    /// 替代原全量 plans.Where + diagnostics.Where 扫描。</summary>
    private void BuildFileIndexes()
    {
        var plansIndex = new Dictionary<string, List<ConcurrencyRegionPlan>>(
            StringComparer.OrdinalIgnoreCase);
        var diagsIndex = new Dictionary<string, List<ConcurrencyDiagnostic>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var plan in _analysis!.Plans)
        {
            foreach (var touched in plan.TouchedFiles)
            {
                if (!plansIndex.TryGetValue(touched, out var list))
                    plansIndex[touched] = list = new List<ConcurrencyRegionPlan>();
                list.Add(plan);
            }
        }

        foreach (var diagnostic in _analysis.Diagnostics)
        {
            if (diagnostic.FilePath == null) continue;
            if (!diagsIndex.TryGetValue(diagnostic.FilePath, out var list))
                diagsIndex[diagnostic.FilePath] = list = new List<ConcurrencyDiagnostic>();
            list.Add(diagnostic);
        }

        _plansByFile = plansIndex;
        _diagnosticsByFile = diagsIndex;
    }

    /// <summary>方法级 region plan（确定性排序）。</summary>
    public IReadOnlyList<ConcurrencyRegionPlan> Plans
    {
        get
        {
            if (_analysis == null)
                throw new InvalidOperationException(
                    "Preprocessor has not analyzed a compilation yet.");
            return _analysis.Plans;
        }
    }

    /// <summary>排除（回退）一组 plan：对应方法整体不再改写，重放从原始副本驱动。</summary>
    public void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    /// <summary>
    /// 逐文件改写（Data flow §4.2 第 3 步）。Changed=false 返回 null，runner 字节级复制；
    /// 统计合并进 <paramref name="statistics"/>，诊断补 RelativePath 后并入
    /// <paramref name="diagnostics"/>。
    /// </summary>
    public ConcurrencyFileResult? RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        ConcurrencyStatistics statistics,
        List<ConcurrencyDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        // 未显式 Bind 时兜底：由传入的 SemanticModel 反查编译并延迟分析（测试友好）。
        if (_analysis == null)
        {
            _compilation ??= semanticModel.Compilation as CSharpCompilation;
            Analyze();
        }

        // 性能：用 O(1) 文件索引替代全量 diagnostics.Where 扫描。
        if (_diagnosticsByFile != null
            && _diagnosticsByFile.TryGetValue(filePath, out var fileDiagnostics))
        {
            foreach (var diagnostic in fileDiagnostics)
                diagnostics.Add(diagnostic);
        }

        CountLockStatements(root, statistics);
        MergeAnalysisCounters(statistics);

        // 性能：用 O(1) 文件索引替代全量 plans.Where 扫描。
        List<ConcurrencyRegionPlan>? candidatePlans = null;
        if (_plansByFile != null && _plansByFile.TryGetValue(filePath, out var indexedPlans))
            candidatePlans = indexedPlans;

        // 本文件的 plan（active + skipped；被排除的 plan 不参与改写）。
        var filePlans = candidatePlans != null
            ? candidatePlans.Where(p => !_excludedPlanIds.Contains(p.Id)).ToList()
            : new List<ConcurrencyRegionPlan>();
        if (filePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return null;
        }

        var rewriter = new LockRewriter(_options, filePlans, filePath, statistics, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        rewriter.FinalizeCounters();

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return null;
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            // 单文件预筛失败保留原文；真正裁决在整树重编译（CLI 驱动），同 RR。
            statistics.FilesSkippedClean++;
            diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Warning,
                ConcurrencyRule.CN2,
                ConcurrencyDiagnosticCodes.PlanRollback,
                "concurrency-normalize rewrite failed local parse validation; the original " +
                "file was preserved pending whole-tree validation.",
                filePath));
            return null;
        }

        EmitRegionRewrittenInfos(rewriter.RewriteCountsByPlan, diagnostics, filePath);
        statistics.FilesTransformed++;
        return new ConcurrencyFileResult { OutputCode = outputCode, Changed = true };
    }

    /// <summary>
    /// 残余闸门（Data flow §4.2 第 4 步）：重写后扫描输出树全部 System.Threading.Monitor
    /// 绑定站点，与 plan 集合对账；无归属残余 &gt;0 → CS2J2970。
    /// </summary>
    public IReadOnlyList<ConcurrencyDiagnostic> ResidualScan(
        Compilation compilation,
        ConcurrencyStatistics? statistics = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        return ConcurrencyResidualScanner.Scan(
            compilation,
            _analysis?.Plans ?? new List<ConcurrencyRegionPlan>(),
            statistics);
    }

    /// <summary>仅格式化改动区间→整文件格式化→原文候选，每个候选重新解析零错误才返回（三候选，同 JK/RR/C09）。
    /// 性能：复用线程级 AdhocWorkspace（画像 2026-10-18：逐文件 new AdhocWorkspace ≈ 10ms）。</summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        var workspace = SharedWorkspace();
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(ConcurrencySyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(ConcurrencySyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span)
                    .Distinct()
                    .OrderBy(s => s.Start)
                    .ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch
            {
                // 局部格式化失败不致命。
            }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { /* 忽略 */ }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, SharedParseOptions());
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }

    private static void EmitRegionRewrittenInfos(
        IReadOnlyDictionary<string, (int Pairs, int TryEnters, int Waits)> rewriteCountsByPlan,
        List<ConcurrencyDiagnostic> diagnostics,
        string filePath)
    {
        foreach (var (planId, counts) in rewriteCountsByPlan.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (counts.Pairs == 0 && counts.TryEnters == 0 && counts.Waits == 0) continue;
            var qualifiedName = planId.StartsWith("CN:", StringComparison.Ordinal)
                ? planId[3..]
                : planId;
            diagnostics.Add(new ConcurrencyDiagnostic(
                ConcurrencySeverity.Info,
                ConcurrencyRule.CN2,
                ConcurrencyDiagnosticCodes.RegionRewritten,
                $"rewrote {counts.Pairs} Enter/Exit pair(s) into lock statements, " +
                $"{counts.TryEnters} TryEnter site(s) into the canonical lockTaken form and " +
                $"{counts.Waits} Wait overload(s).",
                filePath,
                0,
                0,
                qualifiedName));
        }
    }

    private static void CountLockStatements(CompilationUnitSyntax root, ConcurrencyStatistics statistics)
    {
        // 性能：对不含 lock 关键字的文件跳过整树 DescendantNodes 遍历
        //（画像 2026-10-18：933 文件中仅 61 个含 lock，其余 872 个白走一趟
        // ~5ms/文件 ≈ 4.4s）。用 ToString().Contains 做轻量预筛——
        // lock 语句在 C# 中只能用关键字 'lock' 引入，文本级检查足够安全。
        var text = root.ToString();
        if (!text.Contains("lock", StringComparison.Ordinal))
            return;
        var locks = root.DescendantNodes().OfType<LockStatementSyntax>().ToList();
        statistics.LockStatementsSeen += locks.Count;
        statistics.LockNestedRegions += locks.Count(l =>
            l.Ancestors().Any(a => a is LockStatementSyntax));
    }

    /// <summary>Analyzer 检测侧全局计数（CN-6/7/9/11/2920/2910）只并入一次；逐文件计数不在此列。</summary>
    private void MergeAnalysisCounters(ConcurrencyStatistics statistics)
    {
        if (_analysis == null || _analysisCountersMerged) return;
        _analysisCountersMerged = true;

        foreach (var (kind, count) in _analysis.IdentityAuditsByKind)
            statistics.IdentityAuditsByKind[kind] =
                statistics.IdentityAuditsByKind.TryGetValue(kind, out var existing)
                    ? existing + count
                    : count;
        foreach (var (type, count) in _analysis.OutOfScopeSitesByType)
            statistics.OutOfScopeSitesByType[type] =
                statistics.OutOfScopeSitesByType.TryGetValue(type, out var existing)
                    ? existing + count
                    : count;

        statistics.VolatileFieldsPrimitive += _analysis.VolatileFieldsPrimitive;
        statistics.VolatileFieldsNonPrimitive += _analysis.VolatileFieldsNonPrimitive;
        statistics.VolatileStaticApiSites += _analysis.VolatileStaticApiSites;
        statistics.OwnershipWarnings += _analysis.OwnershipWarnings;
        statistics.InfeasibleSites += _analysis.InfeasibleSites;
        statistics.WaitSites += _analysis.WaitSites;
        statistics.WaitSitesNoTimeout += _analysis.WaitSitesNoTimeout;
        statistics.WaitSitesIntMs += _analysis.WaitSitesIntMs;
        statistics.WaitSitesTimeSpan += _analysis.WaitSitesTimeSpan;
        statistics.WaitSitesExitContext += _analysis.WaitSitesExitContext;
        statistics.PulseSites += _analysis.PulseSites;
        statistics.PulseAllSites += _analysis.PulseAllSites;

        // 跳过痕迹按诊断码分桶（2910/2921/2930：区域原样不动的站点）。
        foreach (var diagnostic in _analysis.Diagnostics)
        {
            if (diagnostic.Code is ConcurrencyDiagnosticCodes.NonCanonicalStructure
                or ConcurrencyDiagnosticCodes.LockOwnershipUnproven
                or ConcurrencyDiagnosticCodes.WaitExitContext
                or ConcurrencyDiagnosticCodes.AwaitInLockMirror)
            {
                statistics.SkippedByReason[diagnostic.Code] =
                    statistics.SkippedByReason.TryGetValue(diagnostic.Code, out var existing)
                        ? existing + 1
                        : 1;
            }
        }
    }
}
