using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// Entry point for the local function lift preprocessor.
/// In project mode, a single instance is reused across files to share
/// the cross-file naming table.
/// </summary>
public sealed class LocalFunctionLiftPreprocessor
{
    private readonly Dictionary<string, HashSet<string>> _crossFileNameTable = new();

    public LocalFunctionLiftResult Preprocess(
        string sourceCode,
        SemanticModel? model = null,
        LocalFunctionLiftOptions? options = null)
    {
        options ??= new LocalFunctionLiftOptions();
        var stats = new LocalFunctionLiftStatistics { FilesScanned = 1 };
        var diagnostics = new List<LocalFunctionLiftDiagnostic>();

        SemanticModel? resolvedModel = model;
        if (resolvedModel == null)
        {
            resolvedModel = BuildStandaloneModel(sourceCode, out var parseError);
            if (parseError != null)
            {
                stats.FilesSkippedClean = 1;
                return new LocalFunctionLiftResult(
                    sourceCode, false, Array.Empty<LocalFunctionLiftDiagnostic>(), stats);
            }
        }

        var tree = resolvedModel.SyntaxTree;
        var root = (CompilationUnitSyntax)tree.GetRoot();

        if (!root.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Any())
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, Array.Empty<LocalFunctionLiftDiagnostic>(), stats);
        }

        var analyzer = new LocalFunctionLiftAnalyzer(
            resolvedModel, options, stats, diagnostics, _crossFileNameTable);
        var plans = analyzer.Analyze(root);

        if (plans.All(p => p.Skipped))
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, diagnostics, stats);
        }

        var rewriter = new LocalFunctionLiftRewriter(
            resolvedModel, plans, options, stats);
        rewriter.PrepareMethodDeclarations();
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        if (!rewriter.Changed)
        {
            stats.FilesSkippedClean = 1;
            return new LocalFunctionLiftResult(
                sourceCode, false, diagnostics, stats);
        }

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(newRoot, workspace);
        var code = formatted.ToFullString();
        stats.FilesTransformed = 1;

        return new LocalFunctionLiftResult(code, true, diagnostics, stats);
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

        var compilation = CSharpCompilation.Create("local-fn-standalone",
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
