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

        var isVoid = method.ReturnType is PredefinedTypeSyntax pts &&
                     pts.Keyword.IsKind(SyntaxKind.VoidKeyword);
        var returnsThis = body.DescendantNodes().OfType<ReturnStatementSyntax>()
            .Any(r => r.Expression is ThisExpressionSyntax);
        var isOtherReturn = !isVoid && !returnsThis;

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

        // 5. Handle based on migration type
        if (isVoid)
        {
            // void → struct return: change return type, add `return result;`
            var returnType = (TypeSyntax)SyntaxFactory.IdentifierName(_structName);
            var returnStatement = SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName("result"))
                .WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            newBody = newBody.AddStatements(returnStatement);
            return method.WithReturnType(returnType).WithBody(newBody);
        }
        else if (isOtherReturn)
        {
            // Other return → add out parameter, insert `newStatus = result;` before returns
            var outParam = SyntaxFactory.Parameter(SyntaxFactory.Identifier("newStatus"))
                .WithType(SyntaxFactory.IdentifierName(_structName))
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.OutKeyword).WithTrailingTrivia(SyntaxFactory.Space));

            // Insert `newStatus = result;` before each return statement
            newBody = (BlockSyntax)new InsertOutAssignmentRewriter().Visit(newBody)!;

            return method
                .AddParameterListParameters(outParam)
                .WithBody(newBody);
        }
        else
        {
            // return-this → just return result (already handled by ReturnThisRewriter)
            return method.WithBody(newBody);
        }
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

    /// <summary>Inserts `newStatus = result;` before each return statement for OtherReturnToOut.</summary>
    private sealed class InsertOutAssignmentRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            var assignment = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName("newStatus"),
                    SyntaxFactory.IdentifierName("result")));

            // Return a block containing the assignment + original return
            // We use a trick: wrap in a block if inside a block, otherwise just prepend
            return node.WithLeadingTrivia(
                node.GetLeadingTrivia()
                    .Add(SyntaxFactory.CarriageReturnLineFeed)
                    .Add(SyntaxFactory.Whitespace("        ")));
        }

        public override SyntaxNode? VisitBlock(BlockSyntax node)
        {
            var newStatements = new SyntaxList<StatementSyntax>();
            foreach (var statement in node.Statements)
            {
                if (statement is ReturnStatementSyntax)
                {
                    // Insert `newStatus = result;` before the return
                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName("newStatus"),
                            SyntaxFactory.IdentifierName("result")))
                        .WithLeadingTrivia(statement.GetLeadingTrivia())
                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
                    newStatements = newStatements.Add(assignment);
                    newStatements = newStatements.Add(statement.WithLeadingTrivia(SyntaxFactory.Whitespace("        ")));
                }
                else
                {
                    newStatements = newStatements.Add(statement);
                }
            }
            return node.WithStatements(newStatements);
        }
    }
}
