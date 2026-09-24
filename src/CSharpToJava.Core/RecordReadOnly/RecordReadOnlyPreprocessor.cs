using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.RecordReadOnly;

/// <summary>
/// Record → readonly record 预处理器入口。三阶段（分析→计划→重写）在首次
/// RewriteFile 时基于 <see cref="SemanticModel.Compilation"/> 构建一次并缓存；
/// 项目驱动方对同一编译的每个文件调用一次 RewriteFile。计划级回退（FB-5）
/// 由 CLI 驱动通过 <see cref="ExcludePlans"/> 后用原始文本重新驱动。
/// </summary>
public sealed partial class RecordReadOnlyPreprocessor
{
    private readonly RecordReadOnlyOptions _options;
    private RecordAnalysis? _analysis;
    private ConversionPlan? _plan;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    public RecordReadOnlyPreprocessor(RecordReadOnlyOptions? options = null)
    {
        _options = options ?? new RecordReadOnlyOptions();
    }

    public RecordReadOnlyFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        RecordReadOnlyStatistics statistics,
        List<RecordReadOnlyDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();

        if (!IsRecordDirty(root))
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        EnsureBuilt(semanticModel);

        var filePath = semanticModel.SyntaxTree.FilePath;
        var activePlans = _plan!.Plans
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        RecordReadOnlyAnalyzer.ReportSkippedRecords(
            _plan, _excludedPlanIds, filePath, statistics, diagnostics, _options);

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        var context = new RewriteContext(
            semanticModel, _analysis!, _plan!, activePlans, _excludedPlanIds,
            statistics, diagnostics, filePath);

        var newRoot = ApplyRewriters(root, context);

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            statistics.FilesSkippedClean++;
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            // 单文件预筛失败先保留原文；真正裁决在整树重编译（CLI 驱动），
            // 这里不做单文件回退依据（规范 §3.2/§6）。
            statistics.FilesSkippedClean++;
            diagnostics.Add(new RecordReadOnlyDiagnostic(
                RecordReadOnlySeverity.Warning, RecordReadOnlyDiagnosticCode.RR031,
                "record-readonly rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new RecordReadOnlyFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new RecordReadOnlyFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(SemanticModel model)
    {
        if (_analysis != null && _plan != null) return;
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(model.Compilation);
        _plan = new PlanBuilder(_options, FeatureCaps.Current).Build(_analysis);
    }

    private static CompilationUnitSyntax ApplyRewriters(CompilationUnitSyntax root, RewriteContext context)
    {
        // 顺序：先迁移方法（签名/体/调用点），再改声明，再接口连带，最后残余用点。
        // 每个用点在计划阶段被唯一归类，故四个 rewriter 不会重复处理同一节点。
        var afterMethods = (CompilationUnitSyntax)new MethodMigrator(context).Visit(root)!;
        var afterDecls = (CompilationUnitSyntax)new DeclarationRewriter(context).Visit(afterMethods)!;
        var afterInterfaces = (CompilationUnitSyntax)new InterfaceRippler(context).Visit(afterDecls)!;
        var afterSites = (CompilationUnitSyntax)new MutationSiteRewriter(context).Visit(afterInterfaces)!;
        return afterSites;
    }

    internal static bool IsRecordDirty(SyntaxNode root) =>
        root.DescendantNodes().OfType<RecordDeclarationSyntax>().Any();

    /// <summary>仅格式化改动区间→整文件格式化→原文候选，每个候选重新解析零错误才返回。</summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(RecordSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(RecordSyntaxFacts.RewriteAnnotation.Kind)))
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

    // ----- FB-5 计划级回退支持（CLI 驱动使用） -----

    internal IReadOnlyList<RecordPlan> Plans
    {
        get { EnsureBuiltForInspection(); return _plan!.Plans; }
    }

    private void EnsureBuiltForInspection()
    {
        // 测试/CLI 可能不经过 RewriteFile 直接查计划；此时延迟构建由驱动保证编译已就绪。
        if (_plan != null) return;
        throw new InvalidOperationException("Preprocessor has not analyzed a compilation yet.");
    }

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal void BindCompilation(Compilation compilation)
    {
        _analysis = new RecordReadOnlyAnalyzer(_options).Analyze(compilation);
        _plan = new PlanBuilder(_options, FeatureCaps.Current).Build(_analysis);
    }

    /// <summary>
    /// 把整树重编译产生的新错误归属到唯一 record 计划。归属键：文件 + 错误所在最近
    /// 类型/成员，对计划触碰清单求唯一计划；多计划共享或对不上 → false（整轮回退）。
    /// </summary>
    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        planId = string.Empty;
        if (_plan == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.ToNameString();
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetNameString();

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plan.Plans)
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
}

internal static class RecordDeclarationNaming
{
    public static string ToNameString(this BaseTypeDeclarationSyntax type) => type.Identifier.Text;

    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
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
