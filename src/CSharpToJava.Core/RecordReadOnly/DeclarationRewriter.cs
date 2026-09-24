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

        // readonly 必须排在 partial 之前，否则 CS1585。
        // 找到 partial 的位置，把 readonly 插在其前；没有 partial 则追加到末尾。
        var mods = visited.Modifiers;
        var partialIndex = -1;
        for (var i = 0; i < mods.Count; i++)
        {
            if (mods[i].IsKind(SyntaxKind.PartialKeyword))
            {
                partialIndex = i;
                break;
            }
        }
        var newMods = partialIndex >= 0
            ? mods.Insert(partialIndex, modifier)
            : SyntaxFactory.TokenList(mods.Concat(new[] { modifier }));

        return visited
            .WithModifiers(newMods)
            .WithAdditionalAnnotations(RecordSyntaxFacts.RewriteAnnotation);
    }
}
