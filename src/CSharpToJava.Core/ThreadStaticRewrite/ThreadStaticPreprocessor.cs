using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ThreadStaticRewrite;

/// <summary>
/// Core entry for the thread-static pass (C09): bind the whole-tree compilation once,
/// analyze symbol-level field plans, rewrite one file at a time, and run the residual gate.
/// </summary>
public sealed class ThreadStaticPreprocessor
{
    private Compilation? _compilation;
    private ThreadStaticOptions _options = new();
    private List<ThreadStaticFieldPlan> _plans = new();
    private IReadOnlyList<ThreadStaticDiagnostic> _analysisDiagnostics = Array.Empty<ThreadStaticDiagnostic>();

    /// <summary>Field plans produced by the last <see cref="Analyze"/> call.</summary>
    public IReadOnlyList<ThreadStaticFieldPlan> Plans => _plans;

    public void BindCompilation(Compilation compilation) => _compilation = compilation;

    public IReadOnlyList<ThreadStaticFieldPlan> Analyze(ThreadStaticOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        _options = options ?? new ThreadStaticOptions();
        var analysis = new ThreadStaticAnalyzer(_compilation, _options).Analyze();
        _plans = analysis.Plans.ToList();
        _analysisDiagnostics = analysis.Diagnostics;
        return _plans;
    }

    /// <summary>Deactivates plans by field id (e.g. overrides or rollback ownership). Returns the number excluded.</summary>
    public int ExcludePlans(IReadOnlySet<string> fieldIds)
    {
        var excluded = 0;
        foreach (var plan in _plans)
        {
            if (plan.Active && fieldIds.Contains(plan.Id))
            {
                plan.Active = false;
                plan.FailCode = ThreadStaticCodes.PlanNotSelfContained;
                plan.FailReason = "Plan excluded by configuration or rollback ownership.";
                excluded++;
            }
        }

        return excluded;
    }

    /// <summary>Rewrite the syntax tree backing <paramref name="model"/> (the same tree instance the plans were built from).</summary>
    public ThreadStaticFileResult RewriteFile(SemanticModel model)
    {
        ArgumentNullException.ThrowIfNull(_compilation);
        var stats = new ThreadStaticStatistics { FilesScanned = 1 };
        var diagnostics = new List<ThreadStaticDiagnostic>();
        var tree = model.SyntaxTree;

        var sitesByNode = new Dictionary<SyntaxNode, ReferenceSite>();
        var plansByDeclaration = new Dictionary<FieldDeclarationSyntax, IReadOnlyList<ThreadStaticFieldPlan>>();

        foreach (var plan in _plans)
        {
            if (plan.Declaration.SyntaxTree == tree)
            {
                stats.FieldsDetected++;
                if (!plan.Active)
                {
                    stats.FieldsSkipped++;
                    if (plan.FailCode != null)
                    {
                        stats.SkippedByReason[plan.FailCode] =
                            stats.SkippedByReason.GetValueOrDefault(plan.FailCode) + 1;
                    }

                    stats.InfeasibleSites += plan.Sites.Count(s =>
                        s.Kind is ThreadStaticSiteKind.RefOut or ThreadStaticSiteKind.StructMutation);
                }
            }

            if (!plan.Active) continue;

            foreach (var site in plan.Sites)
            {
                if (site.Node.SyntaxTree == tree)
                {
                    sitesByNode[site.Node] = site;
                }
            }
        }

        // Register declarations that carry at least one active variable (may split with skipped siblings).
        foreach (var declaration in _plans.Where(p => p.Active && p.Declaration.SyntaxTree == tree)
                     .Select(p => p.Declaration).Distinct())
        {
            plansByDeclaration[declaration] = _plans
                .Where(p => p.Declaration == declaration)
                .OrderBy(p => p.Declarator.SpanStart)
                .ToList();
        }

        AppendAnalysisDiagnostics(diagnostics, tree.FilePath);

        if (sitesByNode.Count == 0 && plansByDeclaration.Count == 0)
        {
            stats.FilesSkippedClean = 1;
            return new ThreadStaticFileResult(tree.ToString(), false, diagnostics, stats);
        }

        var root = (CompilationUnitSyntax)tree.GetRoot();
        var rewriter = new ThreadStaticRewriter(sitesByNode, plansByDeclaration, stats);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        if (newRoot == root)
        {
            stats.FilesSkippedClean = 1;
            return new ThreadStaticFileResult(tree.ToString(), false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new ThreadStaticFileResult(FormatAndValidate(newRoot), true, diagnostics, stats);
    }

    /// <summary>M1 residual gate: returns the count and locations of remaining ThreadStaticAttribute instances.</summary>
    public (int Count, IReadOnlyList<Location> Locations) ResidualScan(Compilation rewrittenCompilation)
        => ThreadStaticResidualScanner.Scan(rewrittenCompilation);

    private void AppendAnalysisDiagnostics(List<ThreadStaticDiagnostic> diagnostics, string filePath)
    {
        diagnostics.AddRange(_analysisDiagnostics.Where(d => d.FilePath == filePath));
    }

    /// <summary>
    /// Normalizes the rewritten unit to a canonical layout; falls back to the raw tree text
    /// if normalization fails for any reason (the node set is small, so full-file formatting is sufficient).
    /// </summary>
    internal static string FormatAndValidate(SyntaxNode root)
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
