using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.RecordReadOnly;

internal sealed class DeclarationRewriter : CSharpSyntaxRewriter
{
    private readonly RewriteContext _context;
    public DeclarationRewriter(RewriteContext context) => _context = context;

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax)base.VisitRecordDeclaration(node)!;
        var symbol = _context.Model.GetDeclaredSymbol(node);
        var plan = symbol == null ? null
            : _context.ActivePlans.FirstOrDefault(p =>
                SymbolEqualityComparer.Default.Equals(p.Symbol, symbol));
        if (plan is not { Outcome: PlanOutcome.Converted }) return visited;
        if (plan.Kind != RecordKind.Struct) return visited; // RC-1：class 无标记
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword))) return visited;

        var modifier = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return visited
            .WithModifiers(SyntaxFactory.TokenList(visited.Modifiers.Concat(new[] { modifier })))
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
