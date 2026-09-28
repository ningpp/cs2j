using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.JavaKeywords;

/// <summary>
/// Java 保留字归一化预处理器入口。三阶段（分析→计划→重写）在首次
/// RewriteFile 时基于 SemanticModel.Compilation 构建一次并缓存。
/// </summary>
public sealed partial class JavaReservedWordPreprocessor
{
    private readonly JavaKeywordOptions _options;
    private JavaKeywordAnalysis? _analysis;
    private ConversionPlan? _plan;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    public JavaReservedWordPreprocessor(JavaKeywordOptions? options = null)
    {
        _options = options ?? new JavaKeywordOptions();
    }

    public JavaKeywordFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        JavaKeywordStatistics statistics,
        List<JavaKeywordDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt(semanticModel);

        var activePlans = _plan!.Plans
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        // 发出不可行运算符 plan 的 JK020 诊断
        foreach (var p in activePlans.Where(p =>
            p.Kind == JavaKeywordPlanKind.OperatorTrueFalse && !p.Feasible))
        {
            var loc = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
            var line = 0;
            var col = 0;
            if (loc != null)
            {
                var span = loc.GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                col = span.StartLinePosition.Character + 1;
            }
            diagnostics.Add(new JavaKeywordDiagnostic(
                JavaKeywordSeverity.Warning,
                JavaKeywordDiagnosticCode.JK020,
                p.FailReason ?? "Operator plan infeasible (JK-8)",
                filePath,
                line,
                col,
                p.SymbolName));
            statistics.PlansSkipped++;
            if (!statistics.SkippedByReason.TryGetValue(JavaKeywordDiagnosticCode.JK020, out var cnt2))
                cnt2 = 0;
            statistics.SkippedByReason[JavaKeywordDiagnosticCode.JK020] = cnt2 + 1;
        }

        // 发出 skip 配置排除的运算符 plan 的 JK010 诊断
        foreach (var p in activePlans.Where(p =>
            p.Kind == JavaKeywordPlanKind.OperatorTrueFalse && p.Outcome == PlanOutcome.UserSkipped))
        {
            var loc = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
            var line = 0;
            var col = 0;
            if (loc != null)
            {
                var span = loc.GetLineSpan();
                line = span.StartLinePosition.Line + 1;
                col = span.StartLinePosition.Character + 1;
            }
            diagnostics.Add(new JavaKeywordDiagnostic(
                JavaKeywordSeverity.Info,
                JavaKeywordDiagnosticCode.JK010,
                "Operator plan skipped by overrides.skip configuration",
                filePath,
                line,
                col,
                p.SymbolName));
        }

        if (activePlans.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        var context = new RewriteContext(
            semanticModel, _analysis!, _plan!, activePlans, _excludedPlanIds,
            statistics, diagnostics, filePath);

        var newRoot = ApplyRewriters(root, context);

        var changed = !newRoot.IsEquivalentTo(root);
        if (!changed)
        {
            // 如果有不可行 plan 的诊断，不算 clean skip
            if (diagnostics.Any(d => d.Code == JavaKeywordDiagnosticCode.JK020
                || d.Code == JavaKeywordDiagnosticCode.JK010))
            {
                statistics.FilesTransformed++;
                return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
            }
            statistics.FilesSkippedClean++;
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new JavaKeywordDiagnostic(
                JavaKeywordSeverity.Warning, JavaKeywordDiagnosticCode.JK031,
                "java-keywords rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new JavaKeywordFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new JavaKeywordFileResult { OutputCode = outputCode, Changed = true };
    }

    private void EnsureBuilt(SemanticModel model)
    {
        if (_analysis != null && _plan != null) return;
        _analysis = new JavaKeywordAnalyzer(_options).Analyze(model.Compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

    private static CompilationUnitSyntax ApplyRewriters(CompilationUnitSyntax root, RewriteContext context)
    {
        var afterName = (CompilationUnitSyntax)new KeywordRenamingRewriter(context).Visit(root)!;
        var afterOps = (CompilationUnitSyntax)new OperatorTrueFalseRewriter(context).Visit(afterName)!;
        return afterOps;
    }

    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(JavaKeywordSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(JavaKeywordSyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span)
                    .Distinct()
                    .OrderBy(s => s.Start)
                    .ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch { }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { }
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
            catch { return false; }
        });
    }

    // ----- 计划级回退支持（CLI 驱动使用） -----

    internal IReadOnlyList<JavaKeywordPlan> Plans
    {
        get { EnsureBuiltForInspection(); return _plan!.Plans; }
    }

    private void EnsureBuiltForInspection()
    {
        if (_plan != null) return;
        throw new InvalidOperationException("Preprocessor has not analyzed a compilation yet.");
    }

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal void BindCompilation(Compilation compilation)
    {
        _analysis = new JavaKeywordAnalyzer(_options).Analyze(compilation);
        _plan = new PlanBuilder(_options).Build(_analysis);
    }

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
            .FirstOrDefault()?.Identifier.Text;
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

internal static class JavaKeywordDeclarationNaming
{
    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
