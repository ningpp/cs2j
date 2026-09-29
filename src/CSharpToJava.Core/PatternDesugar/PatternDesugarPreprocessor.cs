using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.PatternDesugar;

/// <summary>
/// C03 statement-side pattern desugaring public entry point. Lowers pattern
/// switches, non-integer governing switches, catch filters and type-parameter
/// catches into compilable C# (if-chains / guarded catch bodies). Clean files
/// are byte-level preserved. No C#→Java converter code is involved.
/// </summary>
public sealed class PatternDesugarPreprocessor
{
    /// <summary>Ordered component registry; C37 appends expression-side components here.</summary>
    internal static IReadOnlyList<IPatternDesugarComponent> Components { get; } =
        new IPatternDesugarComponent[]
        {
            new SwitchStatementComponent(),
            new CatchClauseComponent(),
        };

    public PatternDesugarResult Preprocess(
        string sourceCode,
        SemanticModel? semanticModel = null,
        PatternDesugarOptions? options = null)
    {
        options ??= new PatternDesugarOptions();
        var stats = new PatternDesugarStatistics { FilesScanned = 1 };
        var diagnostics = new List<PatternDesugarDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!PatternDesugarAnalyzer.IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new PatternDesugarResult(sourceCode, false, diagnostics, stats);
        }

        var builder = new PatternDesugarPlanBuilder(options, semanticModel?.SyntaxTree.FilePath);
        foreach (var component in Components)
            component.Analyze(root, semanticModel, builder);
        diagnostics.AddRange(builder.Diagnostics);
        stats.UnsupportedSkipped = builder.UnsupportedSkipped;

        if (builder.SwitchPlans.Count == 0 && builder.CatchPlans.Count == 0)
        {
            // Dirty syntax but nothing lowerable (e.g. plain int switch, plain catch):
            // explicit PD010/PD011 diagnostics still travel with the result.
            stats.FilesDirtyNoRewrite = 1;
            return new PatternDesugarResult(sourceCode, false, diagnostics, stats);
        }

        var rewriter = new PatternDesugarRewriter(
            new PatternDesugarRewriteContext
            {
                SwitchPlans = builder.SwitchPlans,
                CatchPlans = builder.CatchPlans,
                Options = options,
                Statistics = stats,
                Builder = builder,
                Model = semanticModel,
                FilePath = semanticModel?.SyntaxTree.FilePath,
                Rewriter = null!,
            },
            Components);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();

        stats.FilesTransformed = 1;

        // Residual gate: files with explicit PD010/PD011 are already failed loudly,
        // so they skip the residual recheck (no double reporting).
        if (!builder.HasExplicitNodeErrors)
        {
            var residual = semanticModel != null
                ? PatternDesugarAnalyzer.CountResidualTargets(code, semanticModel)
                : PatternDesugarAnalyzer.CountSyntaxResidualTargets(code);
            stats.ResidualCount = residual;
            if (residual > 0)
            {
                diagnostics.Add(new PatternDesugarDiagnostic(
                    PatternDesugarCodes.ResidualTargets,
                    options.Strict ? PatternDesugarSeverity.Error : PatternDesugarSeverity.Warning,
                    $"PD020: {residual} residual target node(s) after pattern desugaring.",
                    semanticModel?.SyntaxTree.FilePath));
            }
        }

        return new PatternDesugarResult(code, true, diagnostics, stats);
    }
}
