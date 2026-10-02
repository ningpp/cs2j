using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.ClosureLift;

/// <summary>
/// Entry point for the closure-lift preprocessor. Owns the per-run naming
/// table (one instance reused across files in project mode, mirrors E02) and
/// the Formatter step; semantics live in HolderLiftStage (Analyze →
/// AuditOnly rows, or Plan → Generate/Rewrite). AuditOnly keeps every M1
/// guarantee: Changed is false and output equals input byte for byte.
/// </summary>
public sealed class ClosureLiftPreprocessor
{
    private readonly ClosureLiftNaming _naming = new();

    public ClosureLiftResult Preprocess(
        string sourceCode,
        SemanticModel? model = null,
        ClosureLiftOptions? options = null)
    {
        options ??= new ClosureLiftOptions();
        var stats = new ClosureLiftStatistics { FilesScanned = 1 };
        var diagnostics = new List<ClosureLiftDiagnostic>();

        SemanticModel? resolvedModel = model;
        if (resolvedModel == null)
        {
            resolvedModel = BuildStandaloneModel(sourceCode, out var parseError);
            if (parseError != null)
            {
                stats.FilesSkippedClean = 1;
                return new ClosureLiftResult(
                    sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
            }
        }

        var tree = resolvedModel.SyntaxTree;
        var root = (CompilationUnitSyntax)tree.GetRoot();

        // Open the per-file naming session: different files continue the host's
        // class-name index; re-entering the same file (same path + contents) is
        // idempotent instead of advancing it (M1 repeated-run / M2 round-trip).
        _naming.BeginFile(tree.FilePath + "\0" + sourceCode);

        // Fast path: no lambdas and no residual anonymous methods (E05 output).
        if (!root.DescendantNodes().Any(n =>
                n is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            stats.FilesSkippedClean = 1;
            return new ClosureLiftResult(
                sourceCode, false, Array.Empty<ClosureLiftDiagnostic>(), stats);
        }

        var stage = new HolderLiftStage();
        var stageResult = stage.Apply(new ClosureStageInput(
            root,
            resolvedModel,
            options,
            _naming,
            stats,
            diagnostics));

        // Analyzer/planner own CS2J3201-3206 rows; bucket them for statistics.
        foreach (var d in diagnostics)
        {
            if (d.Code != ClosureLiftCodes.AuditRow)
                stats.AddSkipped(d.Code);
        }

        if (!stageResult.Changed)
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(stageResult.Root, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;

        return new ClosureLiftResult(code, true, diagnostics, stats);
    }

    /// <summary>
    /// Tree residual gate (design §7.3): re-runs the read-only capture
    /// analysis on an intermediate-tree file WITHOUT a project compilation
    /// (standalone trusted-assembly model, same construction as the single
    /// file path). Returns the number of mutably captured locals/parameters
    /// still present; holder-lift output is 0 by construction (holder
    /// instances are effectively-final reserved names and field accesses are
    /// member expressions, never candidates).
    /// Parse-broken files are the pass fast-path family and return 0: the
    /// pass never edits them, so they cannot own a closure-lift residual.
    /// </summary>
    public int CountResidualMutableCaptures(string code)
    {
        var model = BuildStandaloneModel(code, out var parseError);
        if (parseError != null)
            return 0;

        var discarded = new List<ClosureLiftDiagnostic>();
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var analysis = new CaptureAnalyzer(model, new ClosureLiftOptions(), discarded).Analyze(root);
        return analysis.AllSymbols.Count;
    }

    private static SemanticModel BuildStandaloneModel(string sourceCode, out string? parseError)
    {
        parseError = null;
        var tree = CSharpSyntaxTree.ParseText(sourceCode);

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            parseError = string.Join("; ", errors.Select(e => e.GetMessage()));
        }

        var compilation = CSharpCompilation.Create("closure-lift-standalone",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetSemanticModel(tree);
    }
}
