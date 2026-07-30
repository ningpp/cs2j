using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class MethodMigrator
{
    private readonly string _structName;

    public MethodMigrator(string structName) => _structName = structName;

    public MethodDeclarationSyntax Migrate(MethodDeclarationSyntax method)
    {
        var body = method.Body;
        if (body == null) return method;

        // 1. Insert `var result = this;` at top
        var resultDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"))
            .WithVariables(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.VariableDeclarator("result")
                    .WithInitializer(SyntaxFactory.EqualsValueClause(
                        SyntaxFactory.ThisExpression())))))
            .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        // 2. Replace `this` with `result` in body
        var newBody = (BlockSyntax)new ThisToResultRewriter().Visit(body)!;

        // 3. Replace `return this;` with `return result;`
        newBody = (BlockSyntax)new ReturnThisRewriter().Visit(newBody)!;

        // 4. Prepend result declaration
        newBody = newBody.WithStatements(
            newBody.Statements.Insert(0, resultDecl));

        // 5. Change return type for void methods
        var isVoid = method.ReturnType is PredefinedTypeSyntax pts &&
                     pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var returnType = isVoid
            ? (TypeSyntax)SyntaxFactory.IdentifierName(_structName)
            : method.ReturnType;

        // 6. Add `return result;` for void methods
        if (isVoid)
        {
            var returnStatement = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newBody = newBody.AddStatements(returnStatement);
        }

        return method.WithReturnType(returnType).WithBody(newBody);
    }

    private sealed class ThisToResultRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
        {
            // Don't replace `this` in `var result = this;` context — handled separately
            return SyntaxFactory.IdentifierName("result").WithTriviaFrom(node);
        }
    }

    private sealed class ReturnThisRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression is ThisExpressionSyntax)
                return node.WithExpression(SyntaxFactory.IdentifierName("result"));
            return base.VisitReturnStatement(node);
        }
    }
}
