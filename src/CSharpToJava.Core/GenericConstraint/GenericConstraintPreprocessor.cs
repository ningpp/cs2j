using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.GenericConstraint;

/// <summary>Per-file result of the generic-constraint (R2/CN) rewrite.</summary>
public sealed record GenericConstraintPreprocessResult(
    string OutputCode,
    bool Changed,
    IReadOnlyList<GenericConstraintDiagnostic> Diagnostics,
    GenericConstraintStatistics Statistics);

/// <summary>
/// Core entry point for the <c>generic-constraint</c> pass (upgrade plan
/// §6.4-R2/§6.5). Whole-tree CN-12 dependencies are bound once through
/// <see cref="BindCompilation"/>; every file is then rewritten independently.
/// Any Error diagnostic rolls the whole file back (strict file-level rollback).
/// </summary>
public sealed class GenericConstraintPreprocessor
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private GenericConstraintAnalysis _analysis = GenericConstraintAnalysis.Empty;

    public GenericConstraintAnalysis Analysis => _analysis;

    /// <summary>Binds and analyzes the whole intermediate-tree compilation.</summary>
    public GenericConstraintAnalysis BindCompilation(CSharpCompilation compilation)
    {
        _analysis = GenericConstraintAnalyzer.Analyze(compilation);
        return _analysis;
    }

    /// <summary>Analyzes (and binds) the supplied compilation.</summary>
    public GenericConstraintAnalysis Analyze(CSharpCompilation compilation)
        => BindCompilation(compilation);

    public GenericConstraintPreprocessResult RewriteFile(
        SemanticModel model,
        GenericConstraintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new GenericConstraintOptions();

        var statistics = new GenericConstraintStatistics { FilesScanned = 1 };
        var originalCode = model.SyntaxTree.GetText().ToString();
        var root = (CompilationUnitSyntax)model.SyntaxTree.GetRoot();

        var rewriter = new GenericConstraintRewriter(
            model, options, statistics, _analysis.DependentParameters);
        var rewritten = (CompilationUnitSyntax)rewriter.Visit(root)!;
        var diagnostics = rewriter.Diagnostics;

        if (diagnostics.Any(diagnostic => diagnostic.Severity == GenericConstraintSeverity.Error))
        {
            statistics.FilesSkippedClean = 1;
            return new GenericConstraintPreprocessResult(
                originalCode, Changed: false, diagnostics, statistics);
        }

        if (!rewriter.Changed)
        {
            statistics.FilesSkippedClean = 1;
            return new GenericConstraintPreprocessResult(
                originalCode, Changed: false, diagnostics, statistics);
        }

        using var workspace = new AdhocWorkspace();
        var formatted = Formatter.Format(rewritten, workspace);
        var code = formatted.ToFullString();

        if (HasNewParseErrors(originalCode, code))
        {
            var rolledBack = diagnostics
                .Append(new GenericConstraintDiagnostic(
                    GenericConstraintSeverity.Error,
                    GenericConstraintRule.Infrastructure,
                    GenericConstraintCodes.ConstraintNotExpressible,
                    "generic-constraint rewrite produced a file with new parse errors; " +
                    "the whole file was rolled back (CN strict file-level rollback)."))
                .ToList();
            statistics.FilesSkippedClean = 1;
            return new GenericConstraintPreprocessResult(
                originalCode, Changed: false, rolledBack, statistics);
        }

        statistics.FilesTransformed = 1;
        return new GenericConstraintPreprocessResult(code, Changed: true, diagnostics, statistics);
    }

    /// <summary>
    /// Convenience single-file entry (tests / standalone tooling): builds a
    /// minimal compilation, binds the whole-tree analysis and rewrites.
    /// </summary>
    public GenericConstraintPreprocessResult Preprocess(
        string sourceCode,
        GenericConstraintOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "generic-constraint-file",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        BindCompilation(compilation);
        return RewriteFile(compilation.GetSemanticModel(tree), options);
    }

    private static bool HasNewParseErrors(string originalCode, string rewrittenCode)
    {
        var before = CSharpSyntaxTree.ParseText(originalCode, ParseOptions)
            .GetDiagnostics()
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        var after = CSharpSyntaxTree.ParseText(rewrittenCode, ParseOptions)
            .GetDiagnostics()
            .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return after > before;
    }
}
