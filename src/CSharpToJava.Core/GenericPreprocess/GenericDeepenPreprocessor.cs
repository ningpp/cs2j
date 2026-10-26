using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.GenericPreprocess;

/// <summary>
/// Core entry for the generic-deepen pass: bind the whole-tree compilation once,
/// analyze symbol-level plans, then rewrite file trees and expose generated files.
/// </summary>
public sealed class GenericDeepenPreprocessor
{
    private Compilation? _compilation;
    private GenericDeepenAnalysis _analysis = GenericDeepenAnalysis.Empty;
    private GenericDeepenOptions _options = new();

    public GenericDeepenAnalysis Analysis => _analysis;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public GenericDeepenAnalysis Analyze(GenericDeepenOptions options)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options;
        _analysis = new GenericDeepenAnalyzer(_compilation, options).Analyze();
        return _analysis;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree instance the plans were built from).</summary>
    public GenericDeepenFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        var stats = new GenericDeepenStatistics
        {
            FilesScanned = 1,
        };
        var diagnostics = new List<GenericDeepenDiagnostic>();
        var anchorsByNode = new Dictionary<SyntaxNode, TokenAnchorPlan>();
        var closuresByNode = new Dictionary<SyntaxNode, ClosureAnchorPlan>();
        var patternsByNode = new Dictionary<SyntaxNode, TypeParamPatternPlan>();
        var catchesByNode = new Dictionary<SyntaxNode, CatchTypeParamPlan>();
        var fieldDeclRemovals = new HashSet<SyntaxNode>();
        var fieldUsages = new Dictionary<SyntaxNode, StaticFieldUsagePlan>();
        foreach (var plan in _analysis.Anchors)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                anchorsByNode[plan.Declaration] = plan;
            }
        }

        foreach (var plan in _analysis.ClosureAnchors)
        {
            if (plan.Closure.SyntaxTree == model.SyntaxTree)
            {
                closuresByNode[plan.Closure] = plan;
            }
        }

        foreach (var plan in _analysis.Patterns)
        {
            if (plan.If.SyntaxTree == model.SyntaxTree)
            {
                patternsByNode[plan.If] = plan;
            }
        }

        foreach (var plan in _analysis.Catches)
        {
            if (plan.Clause.SyntaxTree == model.SyntaxTree)
            {
                catchesByNode[plan.Clause] = plan;
            }
        }

        foreach (var plan in _analysis.StaticFields)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                fieldDeclRemovals.Add(plan.Declaration);
            }

            foreach (var usage in plan.Usages)
            {
                if (usage.Node.SyntaxTree == model.SyntaxTree)
                {
                    fieldUsages[usage.Node] = usage;
                }
            }
        }

        var staticMethodsByNode = new Dictionary<SyntaxNode, StaticMethodPlan>();
        var staticCallSites = new Dictionary<SyntaxNode, StaticMethodCallSitePlan>();
        foreach (var plan in _analysis.StaticMethods)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                staticMethodsByNode[plan.Declaration] = plan;
            }

            foreach (var callSite in plan.CallSites)
            {
                if (callSite.Invocation.SyntaxTree == model.SyntaxTree)
                {
                    staticCallSites[callSite.Invocation] = callSite;
                }
            }
        }

        var ctorTokensByType = new Dictionary<SyntaxNode, ConstructorTokenPlan>();
        var ctorTokenSites = new Dictionary<SyntaxNode, GenericDeepenParamRef>();
        var ctorTokenCallSites = new Dictionary<SyntaxNode, IReadOnlyList<string>>();
        foreach (var plan in _analysis.ConstructorTokens)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                ctorTokensByType[plan.Declaration] = plan;
            }

            foreach (var site in plan.TypeOfSites)
            {
                if (site.Site.SyntaxTree == model.SyntaxTree)
                {
                    ctorTokenSites[site.Site] = site.Param;
                }
            }

            foreach (var callSite in plan.CallSites)
            {
                if (callSite.Creation.SyntaxTree == model.SyntaxTree)
                {
                    ctorTokenCallSites[callSite.Creation] = callSite.TypeArgumentTexts;
                }
            }
        }

        var methodTokensByNode = new Dictionary<SyntaxNode, MethodTokenPlan>();
        var methodTokenSites = new Dictionary<SyntaxNode, string>();
        var methodTokenCallSites = new Dictionary<SyntaxNode, IReadOnlyList<string>>();
        foreach (var plan in _analysis.MethodTokens)
        {
            if (plan.Declaration.SyntaxTree == model.SyntaxTree)
            {
                methodTokensByNode[plan.Declaration] = plan;
            }

            foreach (var site in plan.TypeOfSites)
            {
                if (site.Site.SyntaxTree == model.SyntaxTree)
                {
                    methodTokenSites[site.Site] = GenericDeepenSyntaxFacts.AnchorVariableName(site.Param.Name);
                }
            }

            foreach (var callSite in plan.CallSites)
            {
                if (callSite.Invocation.SyntaxTree == model.SyntaxTree)
                {
                    methodTokenCallSites[callSite.Invocation] = callSite.TypeArgumentTexts;
                }
            }
        }

        if (anchorsByNode.Count == 0 && closuresByNode.Count == 0
            && patternsByNode.Count == 0 && catchesByNode.Count == 0
            && fieldDeclRemovals.Count == 0 && fieldUsages.Count == 0
            && staticMethodsByNode.Count == 0 && staticCallSites.Count == 0
            && ctorTokensByType.Count == 0 && ctorTokenSites.Count == 0 && ctorTokenCallSites.Count == 0
            && methodTokensByNode.Count == 0 && methodTokenSites.Count == 0 && methodTokenCallSites.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            AppendAnalysisDiagnostics(diagnostics, model);
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var rewriter = new GenericDeepenRewriter(
            anchorsByNode, closuresByNode, patternsByNode, catchesByNode, fieldDeclRemovals, fieldUsages,
            staticMethodsByNode, staticCallSites, ctorTokensByType, ctorTokenSites, ctorTokenCallSites,
            methodTokensByNode, methodTokenSites, methodTokenCallSites,
            stats, diagnostics, model);
        var newRoot = rewriter.Visit(root);
        AppendAnalysisDiagnostics(diagnostics, model);
        if (newRoot == root)
        {
            stats.FilesSkippedClean = 1;
            return new GenericDeepenFileResult(model.SyntaxTree.ToString(), false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new GenericDeepenFileResult(TryFormat(newRoot), true, diagnostics, stats);
    }

    /// <summary>Analysis-phase diagnostics (skip gates like CS2J1104/1105) belong to their file's result.</summary>
    private void AppendAnalysisDiagnostics(List<GenericDeepenDiagnostic> diagnostics, SemanticModel model)
    {
        var filePath = model.SyntaxTree.FilePath;
        diagnostics.AddRange(_analysis.Diagnostics.Where(d => d.FilePath == filePath));
    }

    /// <summary>
    /// Runs the §4.5 residual gate against the rewritten, recompiled tree set.
    /// Overrides are loaded from <see cref="GenericDeepenOptions.OverridesPath"/>;
    /// a malformed/unreadable file degrades to a CS2J1120 Warning and runs the gate
    /// without overrides (same convention as the closed-metadata gate).
    /// </summary>
    public GenericDeepenResidualReport ResidualScan(Compilation rewritten, IReadOnlySet<string>? activeTreePaths = null)
    {
        if (!_options.RunResidualScan)
        {
            return GenericDeepenResidualReport.Empty;
        }

        GenericDeepenOverrides? overrides = null;
        var loadDiagnostics = new List<GenericDeepenDiagnostic>();
        if (!string.IsNullOrEmpty(_options.OverridesPath))
        {
            try
            {
                overrides = GenericDeepenOverrides.Load(_options.OverridesPath);
            }
            catch (Exception ex) when (ex is GenericDeepenOverridesException or IOException or System.Text.Json.JsonException)
            {
                loadDiagnostics.Add(new GenericDeepenDiagnostic(
                    GenericDeepenSeverity.Warning, GenericDeepenRule.Residual, GenericDeepenCodes.ResidualFound,
                    $"Failed to load generic-deepen overrides from {_options.OverridesPath}: {ex.Message}; "
                    + "running the residual gate without overrides.",
                    _options.OverridesPath));
            }
        }

        var report = GenericDeepenResidualScanner.Scan(rewritten, _analysis, _options, overrides, activeTreePaths);
        if (loadDiagnostics.Count == 0)
        {
            return report;
        }

        return report with
        {
            Diagnostics = loadDiagnostics.Concat(report.Diagnostics).ToList(),
        };
    }

    private static string TryFormat(SyntaxNode root)
    {
        try
        {
            return root.NormalizeWhitespace().ToFullString();
        }
        catch
        {
            return root.ToFullString();
        }
    }
}
