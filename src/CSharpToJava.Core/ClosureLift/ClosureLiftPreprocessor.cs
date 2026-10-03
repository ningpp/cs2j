using CSharpToJava.Core.Analysis;
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

        // C45 M2 stage table (design §4.1): HolderLiftStage (C32) → the
        // named-method lift (static path) → the display-class consolidation
        // (this/read-only captures, body migration) → the delegate-site
        // rewrite. Every stage receives the same per-file plan bag; plans are
        // frozen once and consumed downstream.
        var bag = new ClosureLiftPlanBag();
        var stages = new IClosureLiftStage[]
        {
            new HolderLiftStage(),
            new NamedMethodLiftStage(),
            new DisplayClassConsolidationStage(),
            new DelegateSiteRewriteStage(),
        };

        var currentRoot = root;
        var changed = false;
        foreach (var stage in stages)
        {
            var stageResult = stage.Apply(new ClosureStageInput(
                currentRoot,
                resolvedModel,
                options,
                _naming,
                stats,
                diagnostics)
            {
                PlanBag = bag,
            });

            currentRoot = stageResult.Root;
            changed |= stageResult.Changed;
        }

        // Analyzer/planner own CS2J3201-3206 + CS2J45xx rows; bucket them.
        foreach (var d in diagnostics)
        {
            if (d.Code != ClosureLiftCodes.AuditRow)
                stats.AddSkipped(d.Code);
        }

        // Plan-level rollback (design §4.1): an Error row anywhere in the file
        // keeps the file verbatim. C32/M1 stages freeze themselves before their
        // own edits; M2's consolidation rows can land after the earlier stages
        // already appended members, so the facade enforces the file-wide
        // discipline once, after all stages ran (禁止半提升).
        if (changed && diagnostics.Any(d => d.Severity == ClosureLiftSeverity.Error))
        {
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);
        }

        if (!changed)
            return new ClosureLiftResult(sourceCode, false, diagnostics, stats);

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(currentRoot, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;
        // The file transformed overall: HolderLiftStage may have counted it
        // clean before the C45 stages lifted its zero-capture lambdas.
        stats.FilesSkippedClean = 0;

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

    /// <summary>
    /// C45 M1 residual counts (design §5-4 gate): re-parses an intermediate-tree
    /// file WITHOUT a project compilation and counts (a) statement lambdas that
    /// are NOT quoted expression-tree lambdas (shared ExpressionTreeFacts
    /// predicate — the standalone model references System.Linq.Expressions so
    /// quoted targets resolve) and (b) residual anonymous methods (each one is
    /// also reported by the adapter as CS2J4503). The cs2j_closure_lift
    /// annotation does not survive the text round-trip, so the gate sees the
    /// durable name-prefix guards only. Parse-broken files return 0: the pass
    /// never edits them (fast-path family).
    /// </summary>
    public (int StatementLambdas, int AnonymousMethods) CountResidualLambdas(string code)
    {
        var model = BuildResidualModel(code, out var parseError);
        if (parseError != null)
            return (0, 0);

        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();
        var statementLambdas = 0;
        foreach (var lambda in root.DescendantNodes().OfType<LambdaExpressionSyntax>())
        {
            if (!ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda))
                statementLambdas += 1;
        }

        var anonymousMethods = root
            .DescendantNodes()
            .OfType<AnonymousMethodExpressionSyntax>()
            .Count();

        return (statementLambdas, anonymousMethods);
    }

    private static SemanticModel BuildResidualModel(string sourceCode, out string? parseError)
    {
        parseError = null;
        var tree = CSharpSyntaxTree.ParseText(sourceCode);

        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            parseError = string.Join("; ", errors.Select(e => e.GetMessage()));
        }

        var compilation = CSharpCompilation.Create("closure-lift-residual",
            new[] { tree },
            references: new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                // Quoted-lambda detection resolves Expression<TDelegate> targets.
                MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression<>).Assembly.Location),
            },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        return compilation.GetSemanticModel(tree);
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
