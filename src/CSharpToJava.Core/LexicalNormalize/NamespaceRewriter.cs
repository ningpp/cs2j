using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace CSharpToJava.Core.LexicalNormalize;

/// <summary>
/// LN-7: rewrite a file-scoped namespace into a block-scoped one. Usings after
/// the file-scoped declaration belong inside the namespace (Roslyn stores them
/// on FileScopedNamespaceDeclarationSyntax.Usings); usings before it stay on the
/// compilation unit. Output is normalized through the Formatter (E07/E11 pattern).
/// </summary>
internal sealed class NamespaceRewriter : CSharpSyntaxRewriter
{
    private int _normalizedCount;

    public int NormalizedCount => _normalizedCount;

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var fileScoped = node.Members
            .OfType<FileScopedNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        if (fileScoped == null)
        {
            return node;
        }

        var namespaceKeyword = SyntaxFactory.Token(SyntaxKind.NamespaceKeyword)
            .WithLeadingTrivia(fileScoped.NamespaceKeyword.LeadingTrivia);

        var block = SyntaxFactory.NamespaceDeclaration(fileScoped.Name)
            .WithNamespaceKeyword(namespaceKeyword)
            .WithExterns(fileScoped.Externs)
            .WithUsings(fileScoped.Usings)
            .WithMembers(fileScoped.Members)
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
            .WithCloseBraceToken(
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken)
                    .WithTrailingTrivia(fileScoped.SemicolonToken.TrailingTrivia));

        var newMembers = node.Members.Replace(fileScoped, block);
        var updated = (CompilationUnitSyntax)node.WithMembers(newMembers);
        _normalizedCount++;

        using var workspace = new AdhocWorkspace();
        return Formatter.Format(updated, workspace);
    }
}
