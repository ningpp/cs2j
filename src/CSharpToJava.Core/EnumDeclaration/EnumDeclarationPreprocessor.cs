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

    /// <summary>
    /// 跨枚举成员初值耦合闭包：若枚举 D 的成员初值引用枚举 S 的成员，则只要 D
    /// 本轮不转换（无计划/Infeasible/Deferred/已排除），S 也必须回滚为真 enum——
    /// 否则 S.M 变成 static readonly 字段，D 的常量初值无法编译（CS0029）。
    /// 依赖传递（A↔B↔C）由不动点收敛。返回除 <paramref name="excluded"/> 外
    /// 本轮还必须排除的 plan id。
    /// </summary>
    internal List<string> ComputeForcedRollbacks(IReadOnlyCollection<string> excluded)
    {
        if (_analysis == null) return new List<string>();

        var active = new HashSet<string>(
            _analysis.Plans
                .Where(p => p.Feasible && !excluded.Contains(p.Id))
                .Select(p => p.Id),
            StringComparer.Ordinal);

        var changed = true;
        var removed = new HashSet<string>(StringComparer.Ordinal);
        while (changed)
        {
            changed = false;
            foreach (var (dependent, sources) in _analysis.InitializerDependencies)
            {
                if (active.Contains(dependent)) continue;
                foreach (var source in sources)
                {
                    if (active.Remove(source))
                    {
                        removed.Add(source);
                        changed = true;
                    }
                }
            }
        }

        // 仅返回原本本轮会转换（feasible 且未被排除）、因耦合被迫回滚的 plan；
        // 本就 Infeasible/Deferred/已排除者不转换，无需重复回滚。
        return removed.Where(id => !excluded.Contains(id)).ToList();
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

        // 该文件不含任何活动 plan 触碰点 → 无改写。Analyzer 建 plan 时已把声明所在
        // 文件记入 plan.TouchedFiles（EnumDeclarationPlan.TouchedFiles 的初值即声明
        // 文件），故「本文件声明了活动 enum」必然蕴含 activePlans 非空；原先的
        // declaresActive（整文件 DescendantNodes 扫描）恒为不可达分支，已删除。
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

    /// <summary>
    /// 把「摘要级」统计与诊断提前落账一次（ED010/ED020/ED040/ED001 等）。等价于原先
    /// 首个 <see cref="RewriteFile"/> 调用顺带完成的那一步；提前后，调用方可以跳过
    /// 那些不含任何活动 plan 触碰的文件而保持诊断集合与顺序完全不变。
    /// </summary>
    internal void PrimeSummary(
        EnumDeclarationStatistics statistics, List<EnumDeclarationDiagnostic> diagnostics)
    {
        if (_analysis == null) return;
        FillSummaryStatistics(statistics);
        ReportSummaryDiagnostics(statistics, diagnostics);
    }

    /// <summary>
    /// 全部活动 plan（可行且未被排除）触碰的文件路径集合。文件在集合内 ⇔ 其
    /// <see cref="RewriteFile"/> 的 activePlans 非空；不在集合内的文件必然返回
    /// unchanged，调用方可整族跳过整树扫描与读盘。
    /// </summary>
    internal HashSet<string> ActiveTouchedFiles()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_analysis == null) return set;
        foreach (var plan in _analysis.Plans)
        {
            if (!plan.Feasible || _excludedPlanIds.Contains(plan.Id)) continue;
            foreach (var file in plan.TouchedFiles) set.Add(file);
        }
        return set;
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
                // ED020 表示该 plan 安全让位、保持真 enum（使用点零改写，整树不会
                // 引入新编译错误，pass 的 success 判定也从不因 ED020 失败——只有
                // ED030 整树验证失败才算）。它是设计内的回退终态而非损坏，恒为
                // Warning；ED010/ED011/ED090 是输入异常/单文件硬约束，保持 Error。
                var infeasibleSeverity =
                    plan.FailCode == EnumDeclarationDiagnosticCode.ED020
                        ? EnumDeclarationSeverity.Warning
                        : EnumDeclarationSeverity.Error;
                foreach (var site in plan.InfeasibleSites)
                {
                    var span = site.Location.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        infeasibleSeverity, plan.FailCode!.Value,
                        site.Reason,
                        site.Location.SourceTree?.FilePath,
                        span.Line + 1, span.Character + 1, plan.EnumQualifiedName));
                }
                if (plan.InfeasibleSites.Count == 0 && plan.FailLocation != null)
                {
                    var span = plan.FailLocation.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new EnumDeclarationDiagnostic(
                        infeasibleSeverity, plan.FailCode!.Value,
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
        // 候选按原顺序惰性求值：第一个可解析零错误的候选立即返回。原先无条件算完
        // 三个候选再取首个可解析者——span 区间格式化几乎总能通过局部解析验证，
        // 整文件 Formatter.Format（单个改动文件实测 ~数十 ms）便纯属浪费。
        using var workspace = new AdhocWorkspace();
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
            {
                var spanFormatted = Formatter.Format(root, spans, workspace).ToFullString();
                if (IsParseClean(spanFormatted)) return spanFormatted;
            }
        }
        catch
        {
            // 局部格式化失败不致命。
        }
        try
        {
            var wholeFormatted = Formatter.Format(root, workspace).ToFullString();
            if (IsParseClean(wholeFormatted)) return wholeFormatted;
        }
        catch
        {
            // 整文件格式化失败不致命。
        }

        var raw = root.ToFullString();
        return IsParseClean(raw) ? raw : null;
    }

    private static bool IsParseClean(string candidate)
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
    }
}
