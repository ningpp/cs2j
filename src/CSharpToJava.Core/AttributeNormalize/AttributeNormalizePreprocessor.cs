using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.AttributeNormalize;

/// <summary>
/// C10 attribute-normalize M1 对外入口（design §4.1/§4.4）。
/// 生命周期：<see cref="BindCompilation"/> → <see cref="Analyze"/> →
/// 逐文件 <see cref="RewriteFile"/> → <see cref="ResidualScan"/>。
/// 可经 <see cref="ExcludePlans"/> 显式排除 plan（排除后声明端属性合法保留）。
/// M1 不做 CLI 适配器接入与注册表翻转。
/// </summary>
public sealed class AttributeNormalizePreprocessor
{
    private readonly AttributeNormalizeOptions _options;

    private CSharpCompilation? _compilation;
    private bool _analyzed;
    private List<CallerInfoPlan> _plans = new();
    private AttributeInventoryReport _inventory = AttributeInventoryReport.Empty;

    private readonly HashSet<string> _excludedPlanIds
        = new(StringComparer.Ordinal);

    // 跨文件去重/一次计数状态
    private readonly HashSet<string> _skipDiagnosticsEmitted
        = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedPlansCounted
        = new(StringComparer.Ordinal);
    private bool _detectedCounted;
    private bool _inventoryEmitted;

    public AttributeNormalizePreprocessor(AttributeNormalizeOptions? options = null)
    {
        _options = options ?? new AttributeNormalizeOptions();
    }

    public void BindCompilation(CSharpCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
        _analyzed = false;
    }

    public void Analyze()
    {
        if (_compilation == null)
            throw new InvalidOperationException("Call BindCompilation before Analyze.");

        var analyzer = new CallerInfoAnalyzer(_options);
        (_plans, _inventory) = analyzer.Analyze(_compilation);
        _analyzed = true;
    }

    public void ExcludePlans(IEnumerable<string> planIds)
    {
        ArgumentNullException.ThrowIfNull(planIds);
        foreach (var id in planIds)
            _excludedPlanIds.Add(id);
    }

    /// <summary>全部 plan（分析后可用，含 Skipped 与被排除项）。</summary>
    public IReadOnlyList<CallerInfoPlan> Plans
    {
        get
        {
            EnsureAnalyzed();
            return _plans;
        }
    }

    /// <summary>全树 attribute inventory。</summary>
    public AttributeInventoryReport Inventory
    {
        get
        {
            EnsureAnalyzed();
            return _inventory;
        }
    }

    /// <summary>
    /// 重写单个文件：发 skip 登记 → active plan 调用点填参/声明去属性 →
    /// 格式化三候选并本地再解析校验。
    /// </summary>
    public AttributeNormalizeFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        AttributeNormalizeStatistics statistics,
        List<AttributeNormalizeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (!_analyzed) Analyze();
        if (!_detectedCounted)
        {
            statistics.CallerPlansDetected += _plans.Count;
            _detectedCounted = true;
        }

        if (!_inventoryEmitted)
        {
            EmitInventoryDiagnostics(statistics, diagnostics);
            _inventoryEmitted = true;
        }

        statistics.FilesScanned++;

        var tree = semanticModel.SyntaxTree;
        var filePath = tree.FilePath;
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var diagnosticStartIndex = diagnostics.Count;

        var filePlans = _plans
            .Where(p => p.TouchedFiles.Contains(filePath))
            .ToList();

        // ----- skip 诊断（每 plan 一次，位置优先声明文件） -----
        foreach (var plan in filePlans)
        {
            if (plan.Outcome != CallerPlanOutcome.Skipped) continue;
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!_skipDiagnosticsEmitted.Add(plan.Id)) continue;

            var location = PickPlanLocation(plan, filePath);
            var linePosition = location?.GetLineSpan().StartLinePosition;
            diagnostics.Add(new AttributeNormalizeDiagnostic(
                _options.Strict
                    ? AttributeNormalizeSeverity.Error
                    : AttributeNormalizeSeverity.Warning,
                plan.FailCode!,
                plan.FailReason!,
                location?.SourceTree?.FilePath ?? filePath,
                linePosition?.Line + 1 ?? 0,
                linePosition?.Character + 1 ?? 0,
                plan.Id));

            statistics.CallerPlansSkipped++;
            statistics.SkippedByReason.TryGetValue(plan.FailCode!, out var rc);
            statistics.SkippedByReason[plan.FailCode!] = rc + 1;
        }

        // ----- active 且未排除 plan -----
        var activePlans = filePlans
            .Where(p => p.Outcome == CallerPlanOutcome.Active
                && !_excludedPlanIds.Contains(p.Id))
            .ToList();

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return BuildResult(sourceCode, false);
        }

        var rewriter = new CallerInfoRewriter(semanticModel, activePlans, statistics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (newRoot.IsEquivalentTo(root, topLevel: false))
        {
            statistics.FilesSkippedClean++;
            return BuildResult(sourceCode, false);
        }

        var output = FormatAndValidate(newRoot);
        if (output == null)
        {
            // 本地解析校验失败：保守保留原文，plan 回滚登记（design §4.4）。
            statistics.FilesDirtyNoRewrite++;
            statistics.PlansRolledBack += activePlans.Count;
            foreach (var plan in activePlans)
            {
                _appliedPlansCounted.Remove(plan.Id);
            }

            diagnostics.Add(new AttributeNormalizeDiagnostic(
                AttributeNormalizeSeverity.Warning,
                AttributeNormalizeDiagnosticCode.CS2J1301,
                "Local parse validation failed after rewrite; file preserved and plans rolled back",
                filePath));

            return BuildResult(sourceCode, false);
        }

        statistics.FilesTransformed++;

        foreach (var plan in activePlans)
        {
            if (!_appliedPlansCounted.Add(plan.Id)) continue;
            statistics.CallerPlansApplied++;
        }

        foreach (var plan in activePlans)
        {
            foreach (var site in plan.Sites)
            {
                if (!site.IsFeasible) continue;
                if (!ReferenceEquals(site.CallNode.SyntaxTree, tree)) continue;
                statistics.RecordFilledSite(site.Kind);
            }
        }

        return BuildResult(output, true);

        AttributeNormalizeFileResult BuildResult(string code, bool changed)
        {
            IReadOnlyList<AttributeNormalizeDiagnostic> fileDiagnostics = diagnostics
                .Skip(diagnosticStartIndex)
                .ToList();
            return new AttributeNormalizeFileResult(code, changed, fileDiagnostics, statistics);
        }
    }

    /// <summary>
    /// 将重写后编译诊断归因到唯一 plan（design §4.4 AN-C7）：
    /// 诊断源 span 被唯一 active plan 触碰才归属；多主/无主不归属。
    /// </summary>
    public bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (!_analyzed) return false;

        if (diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.ContainsKey(tree.FilePath)) return false;

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (plan.Outcome != CallerPlanOutcome.Active) continue;
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (plan.OwnsTouch(tree.FilePath, diagnostic.Location.SourceSpan))
                matched.Add(plan.Id);
        }

        if (matched.Count != 1) return false;
        planId = matched.First();
        return true;
    }

    /// <summary>
    /// 重写后 residual 对账（design §4.4）。applied 集合 = active、未排除、
    /// 源内目标的 plan。
    /// </summary>
    public IReadOnlyList<AttributeNormalizeDiagnostic> ResidualScan(
        Compilation rewrittenCompilation,
        AttributeNormalizeStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(rewrittenCompilation);
        ArgumentNullException.ThrowIfNull(statistics);

        var appliedSourcePlans = _plans
            .Where(p => p.Outcome == CallerPlanOutcome.Active
                && !_excludedPlanIds.Contains(p.Id)
                && p.Kind == CallerPlanKind.SourceDeclaration)
            .ToList();

        var scanner = new AttributeResidualScanner(_options);
        return scanner.Scan(
            rewrittenCompilation,
            _inventory,
            appliedSourcePlans,
            statistics,
            _options.Strict);
    }

    // =====================================================================
    // inventory 诊断（CS2J1310/1320/1321，AN002）
    // =====================================================================

    /// <summary>
    /// 按 inventory 发射桥接面/未知/denylist 诊断并填充统计（整树一次）：
    /// BridgeGap → CS2J1310；白名单外自定义 → CS2J1320；denylist → CS2J1321；
    /// AN002 汇总 Info（ReportInfo 时；CLI 仅 verbose 显示）。
    /// AN-U3：*.AssemblyInfo.cs 内的使用点分类保留但不刷 CS2J1310/1320。
    /// </summary>
    private void EmitInventoryDiagnostics(
        AttributeNormalizeStatistics statistics,
        List<AttributeNormalizeDiagnostic> diagnostics)
    {
        // ----- 统计 -----
        foreach (var occurrence in _inventory.Occurrences)
        {
            statistics.AttributesInventoried.TryGetValue(occurrence.Family, out var cnt);
            statistics.AttributesInventoried[occurrence.Family] = cnt + 1;
        }

        statistics.BridgeGaps = _inventory.CountByStatus(BridgeStatus.BridgeGap);
        statistics.UnknownAttributes = _inventory.CountByStatus(BridgeStatus.Unknown);
        statistics.PlatformAttributesInventoried =
            _inventory.CountByFamily(AttributeFamily.Platform)
            + _inventory.CountByFamily(AttributeFamily.Interop);

        // ----- 逐使用点 -----
        foreach (var occurrence in _inventory.Occurrences)
        {
            var file = occurrence.Location.SourceTree?.FilePath;
            var lineSpan = occurrence.Location.GetLineSpan().StartLinePosition;
            var inAssemblyInfo = IsGeneratedAssemblyInfoFile(file);

            // AN-U2 denylist → CS2J1321（不可降级 Error；AssemblyInfo 过滤不覆盖）。
            if (KnownAttributeCatalog.IsDenied(occurrence.AttributeMetadataName))
            {
                diagnostics.Add(new AttributeNormalizeDiagnostic(
                    AttributeNormalizeSeverity.Error,
                    AttributeNormalizeDiagnosticCode.CS2J1321,
                    $"attribute '{occurrence.AttributeMetadataName}' is on the known unbridgeable denylist; " +
                    "manual handling or attribution to the owning pass is required.",
                    file,
                    lineSpan.Line + 1,
                    lineSpan.Character + 1));
                continue;
            }

            // AN-J2/J3 BridgeGap → CS2J1310（AN-U3 AssemblyInfo 不刷）。
            if (occurrence.Status == BridgeStatus.BridgeGap && !inAssemblyInfo)
            {
                var severity = _options.Strict
                    ? AttributeNormalizeSeverity.Error
                    : AttributeNormalizeSeverity.Warning;
                var evidence = KnownAttributeCatalog.TryGetEntry(
                    occurrence.AttributeMetadataName, out var entry)
                    ? (entry.Evidence.Count > 0 ? entry.Evidence[0] : "n/a")
                    : "n/a";
                var note = entry?.BridgeNote;
                var target = occurrence.TargetSymbol?.ToDisplayString() ?? "?";
                var unread = occurrence.UnreadArguments.Count > 0
                    ? $" unread arguments: {string.Join(", ", occurrence.UnreadArguments)};"
                    : string.Empty;
                var message = $"serialization attribute '{occurrence.AttributeMetadataName}' on '{target}' " +
                    $"has a bridge gap:{unread} evidence: {evidence}." +
                    (note != null ? $" ({note})" : string.Empty);

                diagnostics.Add(new AttributeNormalizeDiagnostic(
                    severity,
                    AttributeNormalizeDiagnosticCode.CS2J1310,
                    message,
                    file,
                    lineSpan.Line + 1,
                    lineSpan.Character + 1));
            }

            // AN-U1 未知自定义 → CS2J1320（strict Error；AN-U3 AssemblyInfo 不刷）。
            if (occurrence.Status == BridgeStatus.Unknown && !inAssemblyInfo)
            {
                var severity = _options.Strict
                    ? AttributeNormalizeSeverity.Error
                    : AttributeNormalizeSeverity.Warning;
                var target = occurrence.TargetSymbol?.ToDisplayString() ?? "?";
                var message = $"unknown custom attribute '{occurrence.AttributeMetadataName}' on '{target}'; " +
                    "choose one of: (1) it is a source-generator/compile-time-only attribute -> add it to " +
                    "overrides.ignoreAttributes; (2) runtime semantics are required -> register a bridge or " +
                    "handle it manually; (3) it has no runtime semantics -> add it to overrides.keepAttributes " +
                    "to silence this warning.";

                diagnostics.Add(new AttributeNormalizeDiagnostic(
                    severity,
                    AttributeNormalizeDiagnosticCode.CS2J1320,
                    message,
                    file,
                    lineSpan.Line + 1,
                    lineSpan.Character + 1));
            }
        }

        // ----- AN002 汇总（Info） -----
        if (_options.ReportInfo)
        {
            diagnostics.Add(new AttributeNormalizeDiagnostic(
                AttributeNormalizeSeverity.Info,
                AttributeNormalizeDiagnosticCode.AN002,
                BuildInventorySummary(),
                null));
        }
    }

    /// <summary>AN002 汇总文本：分 family/状态计数 + BridgeGap 明细头部。</summary>
    private string BuildInventorySummary()
    {
        var lines = new List<string>
        {
            $"attribute inventory summary: {_inventory.Occurrences.Count} occurrence(s)",
        };

        var familyLine = string.Join(", ",
            Enum.GetValues(typeof(AttributeFamily))
                .Cast<AttributeFamily>()
                .Where(f => _inventory.CountByFamily(f) > 0)
                .Select(f => $"{f}={_inventory.CountByFamily(f)}"));
        lines.Add("  by family: " + familyLine);

        var statusLine = string.Join(", ",
            Enum.GetValues(typeof(BridgeStatus))
                .Cast<BridgeStatus>()
                .Where(s => _inventory.CountByStatus(s) > 0)
                .Select(s => $"{s}={_inventory.CountByStatus(s)}"));
        lines.Add("  by status: " + statusLine);

        const int gapHead = 20;
        var gaps = _inventory.Occurrences
            .Where(o => o.Status == BridgeStatus.BridgeGap)
            .Take(gapHead)
            .Select(o =>
            {
                var unread = o.UnreadArguments.Count > 0
                    ? $" [unread: {string.Join(",", o.UnreadArguments)}]"
                    : string.Empty;
                return $"  gap: {o.AttributeMetadataName} @ " +
                    $"{o.TargetSymbol?.ToDisplayString() ?? "?"}{unread}";
            });
        lines.AddRange(gaps);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// AN-U3：*.AssemblyInfo.cs 判定（与 UnsupportedDomainPasses 同款：
    /// 文件名以 .AssemblyInfo.cs 结尾）。
    /// </summary>
    private static bool IsGeneratedAssemblyInfoFile(string? filePath)
        => !string.IsNullOrWhiteSpace(filePath)
            && Path.GetFileName(filePath).EndsWith(
                ".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);

    // =====================================================================
    // 内部 helper
    // =====================================================================

    private void EnsureAnalyzed()
    {
        if (!_analyzed)
            throw new InvalidOperationException("Preprocessor has not analyzed a compilation yet.");
    }

    private static Location? PickPlanLocation(CallerInfoPlan plan, string currentFile)
    {
        if (plan.DeclarationLocation is { } decl
            && string.Equals(
                decl.SourceTree?.FilePath,
                currentFile,
                StringComparison.OrdinalIgnoreCase))
        {
            return decl;
        }

        return plan.Sites
            .FirstOrDefault(s => string.Equals(
                s.FilePath,
                currentFile,
                StringComparison.OrdinalIgnoreCase))
            ?.SiteLocation
            ?? plan.DeclarationLocation
            ?? plan.Sites.FirstOrDefault()?.SiteLocation;
    }

    /// <summary>
    /// 三候选格式化 + 再解析校验（参照 JavaReservedWordPreprocessor）：
    /// 改动区 span 格式化 → 整文件格式化 → 原文，首个零错误候选胜出。
    /// </summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var annotationKind = CallerInfoRewriter.RewriteAnnotation.Kind;
        var candidates = new List<string>();

        using var workspace = new AdhocWorkspace();

        try
        {
            var spans = root
                .GetAnnotatedNodes(annotationKind)
                .Where(n => !n.Ancestors().Any(a => a.HasAnnotations(annotationKind)))
                .Select(n => n.Span)
                .Distinct()
                .OrderBy(s => s.Start)
                .ToList();
            if (spans.Count > 0)
                candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
        }
        catch
        {
            // 下探整文件格式化
        }

        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch
        {
            // 下探原文
        }

        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate,
                    new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics()
                    .Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }
}
