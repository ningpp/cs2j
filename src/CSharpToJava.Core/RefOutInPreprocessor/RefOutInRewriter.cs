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
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats);
        }

        if (semanticModel == null)
        {
            stats.FilesSkippedClean = 1;
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats);
        }

        // Pass 1: Annotate
        var annotator = new RefOutInAliasAnnotator(semanticModel, stats, diagnostics);
        annotator.Annotate();

        // Pass 2+3: Rewrite
        var rewriter = new RefOutInSyntaxRewriter(semanticModel, annotator, stats, diagnostics);
        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;

        // Inject using Cs2jByRef only when the output actually references an
        // unqualified support type. Merely stripping `in`/`scoped` modifiers
        // changes the signature but emits no Cs2jByRef type, so the eager
        // UsedHolder flag alone must not force the using (CS0246 in projects
        // that never receive the support library).
        if (rewriter.UsedHolder && UsesUnqualifiedByRefType(newRoot))
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
            diagnostics.Add(new RefOutInRewriterDiagnostic(
                RefOutInDiagnosticCodes.ReparseFailed,
                "ref/out/in rewrite produced invalid C# syntax; the original file was preserved.",
                RefOutInSeverity.Error));
            return new RefOutInRewriterResult(sourceCode, false, diagnostics, stats);
        }

        stats.FilesTransformed = 1;
        return new RefOutInRewriterResult(
            outputCode, true, diagnostics, stats);
    }

    internal static bool IsDirty(CompilationUnitSyntax root)
    {
        return root.DescendantTokens().Any(t =>
            t.IsKind(SyntaxKind.RefKeyword)
            || t.IsKind(SyntaxKind.OutKeyword)
            || t.IsKind(SyntaxKind.InKeyword)
            || t.IsKind(SyntaxKind.ScopedKeyword));
    }

    private static readonly string[] UnqualifiedByRefTypeNames =
    {
        "ByRef",
        "ReadOnlyByRef",
        "ScopedByRef",
        "ReadOnlyScopedByRef",
        "RefStructByRef",
    };

    /// <summary>
    /// True when the tree references a Cs2jByRef support type by its simple
    /// name (i.e. it needs <c>using Cs2jByRef;</c>). Fully qualified
    /// <c>Cs2jByRef.X</c> usages and plain <c>in</c>-stripped signatures do
    /// not require the directive.
    /// </summary>
    internal static bool UsesUnqualifiedByRefType(CompilationUnitSyntax root)
    {
        foreach (var name in root.DescendantNodes().OfType<GenericNameSyntax>())
        {
            if (!UnqualifiedByRefTypeNames.Contains(name.Identifier.Text))
                continue;

            if (name.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == name)
            {
                continue; // qualified (e.g. Cs2jByRef.ByRef<int>)
            }

            return true;
        }

        return false;
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
