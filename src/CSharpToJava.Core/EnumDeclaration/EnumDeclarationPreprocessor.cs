using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.EnumDeclaration;

/// <summary>
/// C05 Enum 声明归一预处理器公共入口（C#→C#）。三阶段：
/// Analyzer 整树盘点 → Synthesizer 物化 sealed class → Rewriter 整树改写使用点。
/// 首次 BindCompilation/RewriteFile 时建分析并缓存；计划级回退由 CLI 驱动
/// 通过 <see cref="ExcludePlans"/> 后用原始文本重新驱动（ED031）。
/// </summary>
public sealed class EnumDeclarationPreprocessor
{
    private readonly EnumDeclarationOptions _options;
    private EnumAnalysis? _analysis;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);
    private bool _summaryReported;

    public EnumDeclarationPreprocessor(EnumDeclarationOptions? options = null)
    {
        _options = options ?? new EnumDeclarationOptions();
    }

    internal void BindCompilation(Compilation compilation)
    {
        _analysis = new EnumDeclarationAnalyzer(compilation, _options).Analyze();
        _summaryReported = false;
        Prepare();
    }

    internal IReadOnlyList<EnumDeclarationPlan> Plans
        => (IReadOnlyList<EnumDeclarationPlan>?)_analysis?.Plans
            ?? Array.Empty<EnumDeclarationPlan>();

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    public EnumDeclarationFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        EnumDeclarationStatistics statistics,
        List<EnumDeclarationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt(semanticModel.Compilation);
        FillSummaryStatistics(statistics);
        ReportSummaryDiagnostics(statistics, diagnostics);

        var activePlans = _analysis!.Plans
            .Where(p => p.Feasible && !_excludedPlanIds.Contains(p.Id)
                && p.TouchedFiles.Contains(filePath))
            .ToList();

        var declaresActive = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Any(d => activePlans.Any(p => p.Declaration == d));

        if (!declaresActive && activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new EnumDeclarationFileResult { OutputCode = sourceCode, Changed = false };
        }

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new EnumDeclarationFileResult { OutputCode = sourceCode, Changed = false };
        }

        // 使用点改写（跳过 enum 声明子树），再整体替换声明。
        var useSiteRewriter = new EnumUseSiteRewriter(
            semanticModel, _analysis.Plans
                .Where(p => p.Feasible && !_excludedPlanIds.Contains(p.Id)));
        var afterUseSitesNoTemps = (CompilationUnitSyntax)useSiteRewriter.Visit(root)!;
        // switch/is 判定登记的单次求值临时变量在此统一插入声明锚点。
        var afterUseSites = EnumUseSiteRewriter.ApplyHoistedTemps(
            afterUseSitesNoTemps, useSiteRewriter.PendingTemps);
        var declarationRewriter = new EnumDeclarationRewriter(activePlans);
        var newRoot = (CompilationUnitSyntax)declarationRewriter
            .RewriteFileTree(afterUseSites, filePath)!;

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return new EnumDeclarationFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new EnumDeclarationDiagnostic(
                EnumDeclarationSeverity.Warning, EnumDeclarationDiagnosticCode.ED031,
                "enum-declaration rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.",
                filePath, 0, 0, null));
            return new EnumDeclarationFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new EnumDeclarationFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(Compilation compilation)
    {
        if (_analysis != null) return;
        BindCompilation(compilation);
    }

    /// <summary>为全部可行 plan 物化合成声明（D16 命名表落到 plan.Names，供使用点改写）。</summary>
    private void Prepare()
    {
        foreach (var plan in _analysis!.Plans)
        {
            if (plan.Feasible && !_excludedPlanIds.Contains(plan.Id) && plan.Names == null)
                EnumDeclarationSynthesizer.Synthesize(plan);
        }
    }

    private void FillSummaryStatistics(EnumDeclarationStatistics statistics)
    {
        // 分析对同一 compilation 只建一次；计数在首轮 RewriteFile 落账即可，幂等不重复加。
        if (statistics.EnumsScanned != 0) return;
        var plans = _analysis!.Plans;
        statistics.EnumsScanned = plans.Count;
        statistics.EnumsRewritten = plans.Count(p =>
            p.Feasible && !_excludedPlanIds.Contains(p.Id));
        statistics.FlagsCount = plans.Count(p => p is { IsFlags: true, Feasible: true });
        statistics.NonIntCount = plans.Count(p => p is { IsNonInt: true, Feasible: true });
        statistics.ExplicitCount = plans.Count(p => p is { HasExplicitValue: true, Feasible: true });
        statistics.DuplicateCount = plans.Count(p => p is { HasDuplicateValue: true, Feasible: true });
        statistics.PlansSkipped = plans.Count(p => !p.Feasible);
        statistics.PlansDeferred = plans.Count(p => p.Outcome == EnumPlanOutcome.Deferred);
        foreach (var p in plans.Where(p => !p.Feasible && p.FailCode != null))
            statistics.SkippedByReason[p.FailCode.Value] =
                statistics.SkippedByReason.GetValueOrDefault(p.FailCode.Value) + 1;
    }

    private void ReportSummaryDiagnostics(
        EnumDeclarationStatistics statistics, List<EnumDeclarationDiagnostic> diagnostics)
    {
        if (_summaryReported) return;
        _summaryReported = true;

        // ED010（成员常量缺失，plan 不建立）。
        diagnostics.AddRange(_analysis!.ConstantDiagnostics);

        foreach (var plan in _analysis.Plans)
        {
            var excluded = _excludedPlanIds.Contains(plan.Id);

            if (plan.Feasible && !excluded)
            {
                if (_options.ReportInfo)
                {
                    var (file, line, col) = plan.PrimaryLocation();
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        EnumDeclarationSeverity.Info, EnumDeclarationDiagnosticCode.ED001,
                        $"enum '{plan.EnumQualifiedName}' normalized to enum-like sealed class " +
                        $"(underlying {EnumDeclarationSyntaxFacts.GetTypeKeyword(plan.Underlying)}, " +
                        $"{plan.Members.Count} members).",
                        file, line, col, plan.EnumQualifiedName));
                }

                if (plan.CrossFormCastCount > 0)
                {
                    var (file, line, col) = plan.PrimaryLocation();
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        _options.Strict ? EnumDeclarationSeverity.Error : EnumDeclarationSeverity.Warning,
                        EnumDeclarationDiagnosticCode.ED012,
                        $"enum '{plan.EnumQualifiedName}' has {plan.CrossFormCastCount} cross-form " +
                        "cast site(s) against enums left as real enums; integer-boxing bridges were inserted.",
                        file, line, col, plan.EnumQualifiedName));
                }
                if (plan.ResidualE04Count > 0)
                {
                    var (file, line, col) = plan.PrimaryLocation();
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        _options.Strict ? EnumDeclarationSeverity.Error : EnumDeclarationSeverity.Warning,
                        EnumDeclarationDiagnosticCode.ED013,
                        $"enum '{plan.EnumQualifiedName}' has {plan.ResidualE04Count} residual " +
                        "nullable-enum site(s) from the enum bitwise pass.",
                        file, line, col, plan.EnumQualifiedName));
                }
            }
            else if (plan.Outcome == EnumPlanOutcome.Deferred)
            {
                // 静默让位：++/--/算术站点归链中更晚的 numeric-boundary 族，
                // 不是错误；仅在 verbose（ReportInfo）时给一条可追踪信息。
                if (_options.ReportInfo && plan.FailLocation != null)
                {
                    var span = plan.FailLocation.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        EnumDeclarationSeverity.Info, EnumDeclarationDiagnosticCode.ED001,
                        $"enum '{plan.EnumQualifiedName}' kept as a real enum: "
                        + plan.FailReason,
                        plan.FailLocation.SourceTree?.FilePath,
                        span.Line + 1, span.Character + 1, plan.EnumQualifiedName));
                }
            }
            else if (!plan.Feasible)
            {
                foreach (var site in plan.InfeasibleSites)
                {
                    var span = site.Location.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        EnumDeclarationSeverity.Error, plan.FailCode!.Value,
                        site.Reason,
                        site.Location.SourceTree?.FilePath,
                        span.Line + 1, span.Character + 1, plan.EnumQualifiedName));
                }
                if (plan.InfeasibleSites.Count == 0 && plan.FailLocation != null)
                {
                    var span = plan.FailLocation.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        EnumDeclarationSeverity.Error, plan.FailCode!.Value,
                        plan.FailReason ?? "enum normalization is infeasible.",
                        plan.FailLocation.SourceTree?.FilePath,
                        span.Line + 1, span.Character + 1, plan.EnumQualifiedName));
                }
            }

            // ED040 名字契约站点（strict 升 Error）。
            foreach (var site in plan.NameContractSites)
            {
                var span = site.Location.GetLineSpan().StartLinePosition;
                diagnostics.Add(new EnumDeclarationDiagnostic(
                    _options.Strict ? EnumDeclarationSeverity.Error : EnumDeclarationSeverity.Warning,
                    EnumDeclarationDiagnosticCode.ED040,
                    site.Message,
                    site.Location.SourceTree?.FilePath,
                    span.Line + 1, span.Character + 1, plan.EnumQualifiedName));
            }
        }
    }

    /// <summary>
    /// 把整树重编译产生的新错误归属到唯一 plan。归属键：文件 + 错误所在最近
    /// 类型/成员，对计划触碰清单求唯一；多计划共享或对不上 → false（整轮回退，ED030）。
    /// </summary>
    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (_analysis == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.ContainsKey(tree.FilePath)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault() switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text,
            PropertyDeclarationSyntax p => p.Identifier.Text,
            BaseFieldDeclarationSyntax f => f.Declaration.Variables
                .FirstOrDefault()?.Identifier.Text,
            EventDeclarationSyntax e => e.Identifier.Text,
            _ => null,
        };

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _analysis.Plans)
        {
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (plan.OwnsTouch(tree.FilePath, typeName, memberName))
                matched.Add(plan.Id);
        }

        if (matched.Count == 1)
        {
            planId = matched.First();
            return true;
        }
        return false;
    }

    /// <summary>仅格式化改动区间→整文件格式化→原文候选，每个候选重新解析零错误才返回。</summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(EnumDeclarationSyntaxFacts.RewriteAnnotationKind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(EnumDeclarationSyntaxFacts.RewriteAnnotationKind)))
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
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch
            {
                return false;
            }
        });
    }
}
