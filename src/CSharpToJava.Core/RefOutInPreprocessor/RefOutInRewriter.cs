using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.RefOutInPreprocessor;

public sealed class RefOutInRewriter
{
    public RefOutInRewriterResult Rewrite(
        string sourceCode,
        SemanticModel? semanticModel = null,
        RefOutInRewriterOptions? options = null)
    {
        options ??= new RefOutInRewriterOptions();
        var stats = new RefOutInRewriterStatistics { FilesScanned = 1 };
        var diagnostics = new List<RefOutInRewriterDiagnostic>();

        var root = semanticModel != null
            ? (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot()
            : (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(sourceCode).GetRoot();

        if (!IsDirty(root))
        {
            stats.FilesSkippedClean = 1;
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats, Array.Empty<string>());
        }

        if (semanticModel == null)
        {
            stats.FilesSkippedClean = 1;
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats, Array.Empty<string>());
        }

        // Pass 1: Annotate
        var annotator = new RefOutInAliasAnnotator(semanticModel, stats, diagnostics);
        annotator.Annotate();

        // Pass 2+3: Rewrite
        var rewriter = new RefOutInSyntaxRewriter(semanticModel, annotator, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // Inject using Cs2jByRef if needed
        if (rewriter.UsedHolder)
        {
            newRoot = InjectUsing(newRoot);
        }

        // Format and validate
        var outputCode = FormatAndValidate(newRoot, diagnostics);
        if (outputCode == null)
        {
            stats.FilesSkippedClean = 1;
            diagnostics.Add(new RefOutInRewriterDiagnostic(
                "ROI099",
                "ref/out/in rewrite produced invalid C# syntax; the original file was preserved.",
                RefOutInSeverity.Error));
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats, Array.Empty<string>());
        }

        stats.FilesTransformed = 1;
        return new RefOutInRewriterResult(
            outputCode, true, diagnostics, stats,
            annotator.RequiredRefStructTypes);
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
    {
        return root.DescendantTokens().Any(t =>
            t.IsKind(SyntaxKind.RefKeyword)
            || t.IsKind(SyntaxKind.OutKeyword)
            || t.IsKind(SyntaxKind.InKeyword)
            || t.IsKind(SyntaxKind.ScopedKeyword));
    }

    private static CompilationUnitSyntax InjectUsing(CompilationUnitSyntax root)
    {
        var usingName = SyntaxFactory.IdentifierName("Cs2jByRef");
        var usingDirective = SyntaxFactory.UsingDirective(usingName);

        var usings = root.Usings;
        if (usings.Any(u => u.Name?.ToString() == "Cs2jByRef"))
            return root;

        return root.AddUsings(usingDirective);
    }

    private static string? FormatAndValidate(
        CompilationUnitSyntax root,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        using var workspace = new AdhocWorkspace();
        var candidates = new List<string>();
        try
        {
            candidates.Add(Formatter.Format(root, workspace).ToFullString());
        }
        catch { }

        candidates.Add(root.ToFullString());

        foreach (var candidate in candidates)
        {
            var reparsed = CSharpSyntaxTree.ParseText(candidate);
            if (!reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
                return candidate;
        }
        return null;
    }
}
