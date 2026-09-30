using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// C07 struct 值语义补全预处理器入口（设计 §4）。三阶段（分析→计划→重写）整树
/// 一次构建：CLI 适配器在自己的 ExecuteAsync 内对 E13 输出树做整树编译后调用
/// <see cref="BindCompilation"/>，再逐文件调用 <see cref="RewriteFile"/>。计划级回退
/// （SV030/SV031）由 CLI 驱动通过 <see cref="ExcludePlans"/> 后用原始文本重放。
/// 契约形状与 RecordReadOnlyPreprocessor 同形。
/// </summary>
public sealed partial class StructValueSemanticsPreprocessor
{
    private readonly StructValueSemanticsOptions _options;
    private Compilation? _compilation;
    private StructValueSemanticsAnalysis? _analysis;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);
    private bool _planningOutputMerged;

    public StructValueSemanticsPreprocessor(StructValueSemanticsOptions? options = null)
    {
        _options = options ?? new StructValueSemanticsOptions();
    }

    internal StructValueSemanticsOptions Options => _options;

    /// <summary>整树分析入口（CLI 驱动）：残余集合/装箱可达/站点/计划一次算全并缓存。</summary>
    internal void BindCompilation(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        _compilation = compilation;
        _planningOutputMerged = false;
        _analysis = StructValueSemanticsAnalyzer.AnalyzeAndPlan(compilation, _options, _excludedPlanIds);
    }

    /// <summary>
    /// 计划级产物（聚合计数 + SV001/SV010/SV020/SV050/SV051 诊断）在首次 RewriteFile
    /// 时并入本次运行输出一次；即使所有文件都无改写（全部 Skipped/Infeasible）也要暴露。
    /// </summary>
    private void MergePlanningOutput(
        StructValueSemanticsStatistics statistics,
        List<StructValueSemanticsDiagnostic> diagnostics)
    {
        if (_planningOutputMerged) return;
        _planningOutputMerged = true;

        var planning = _analysis!.PlanningStatistics;
        statistics.StructsScanned += planning.StructsScanned;
        statistics.StructsPlanned += planning.StructsPlanned;
        statistics.StructsReusedClone += planning.StructsReusedClone;
        statistics.ShimsGenerated += planning.ShimsGenerated;
        statistics.PlansSkipped += planning.PlansSkipped;
        foreach (var (code, count) in planning.SkippedByReason)
        {
            statistics.SkippedByReason.TryGetValue(code, out var existing);
            statistics.SkippedByReason[code] = existing + count;
        }

        foreach (var diagnostic in _analysis.PlanningDiagnostics)
        {
            if (diagnostic.StructName != null
                && _excludedPlanIds.Contains("SV:" + diagnostic.StructName))
            {
                continue; // 已被 SV031 回退排除的 plan 不再报计划期诊断
            }
            diagnostics.Add(diagnostic);
        }
    }

    internal StructValueSemanticsFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        StructValueSemanticsStatistics statistics,
        List<StructValueSemanticsDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt(semanticModel.Compilation);

        MergePlanningOutput(statistics, diagnostics);

        var activePlans = _analysis!.PlanSet.Plans
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.IsActive
                && p.TouchedFiles.Contains(filePath))
            .ToList();

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new StructValueSemanticsFileResult { OutputCode = sourceCode, Changed = false };
        }

        var rewriter = new CloneInsertionRewriter(
            semanticModel, activePlans, _analysis!, _options, statistics, diagnostics, filePath);
        var newRoot = rewriter.Rewrite(root);

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return new StructValueSemanticsFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            // 单文件预筛失败先保留原文；真正裁决在整树重编译（CLI 驱动）。
            statistics.FilesSkippedClean++;
            diagnostics.Add(new StructValueSemanticsDiagnostic(
                StructValueSemanticsSeverity.Warning, StructValueSemanticsDiagnosticCode.SV031,
                "struct-value-semantics rewrite failed local parse validation; the original " +
                "file was preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new StructValueSemanticsFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new StructValueSemanticsFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(Compilation compilation)
    {
        if (_analysis != null) return;
        BindCompilation(compilation);
    }

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal IReadOnlyList<ClonePlan> Plans
    {
        get
        {
            if (_analysis == null)
                throw new InvalidOperationException(
                    "Preprocessor has not analyzed a compilation yet.");
            return _analysis.PlanSet.Plans;
        }
    }

    /// <summary>
    /// 把整树重编译产生的新错误归属到唯一 struct 计划（文件 + 最近类型/成员，
    /// 对触碰清单求唯一计划；多计划共享或对不上 → false → SV030 全量回退）。
    /// </summary>
    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (_analysis == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetMemberNameString();

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _analysis.PlanSet.Plans)
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
                    .GetAnnotatedNodes(StructValueSemanticsSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation.Kind)))
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

internal static class StructMemberNaming
{
    public static string? GetMemberNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
