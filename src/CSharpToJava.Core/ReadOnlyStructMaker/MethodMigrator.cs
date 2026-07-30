using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

internal sealed class MethodMigrator
{
    private readonly string _structName;
    private readonly HashSet<string> _fieldNames;
    private readonly HashSet<string> _migratedMethodNames;

    public MethodMigrator(string structName, HashSet<string>? fieldNames = null, HashSet<string>? migratedMethodNames = null)
    {
        _structName = structName;
        _fieldNames = fieldNames ?? new HashSet<string>();
        _migratedMethodNames = migratedMethodNames ?? new HashSet<string>();
    }

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

        // 3. Replace implicit field accesses with result.field
        if (_fieldNames.Count > 0)
        {
            newBody = (BlockSyntax)new ImplicitFieldToResultRewriter(_fieldNames).Visit(newBody)!;
        }

        // 3b. Replace bare calls to migrated methods with result = result.Method(args)
        if (_migratedMethodNames.Count > 0)
        {
            newBody = (BlockSyntax)new ImplicitCallRewriter(_migratedMethodNames, method.Identifier.Text).Visit(newBody)!;
        }

        // 4. Replace `return this;` with `return result;`
        newBody = (BlockSyntax)new ReturnThisRewriter().Visit(newBody)!;

        // 5. Prepend result declaration
        newBody = newBody.WithStatements(
            newBody.Statements.Insert(0, resultDecl));

        // 6. Handle based on migration type
        if (isVoid)
        {
            // void → struct return: change return type, replace bare returns with return result, add final return
            newBody = (BlockSyntax)new BareReturnRewriter().Visit(newBody)!;
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

    /// <summary>
    /// Rewrites implicit field accesses (e.g. `_field = value;`) to `result._field = value;`
    /// and implicit field reads (e.g. `if (_field == null)`) to `result._field`.
    /// Only rewrites identifiers that match known struct field names and are not local variables.
    /// </summary>
    private sealed class ImplicitFieldToResultRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _fieldNames;
        private readonly HashSet<string> _localNames = new();

        public ImplicitFieldToResultRewriter(HashSet<string> fieldNames)
        {
            _fieldNames = fieldNames;
        }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            // Track local variable names to avoid rewriting them
            foreach (var variable in node.Declaration.Variables)
                _localNames.Add(variable.Identifier.Text);
            return base.VisitLocalDeclarationStatement(node);
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitForEachStatement(node);
        }

        public override SyntaxNode? VisitParameter(ParameterSyntax node)
        {
            _localNames.Add(node.Identifier.Text);
            return base.VisitParameter(node);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var name = node.Identifier.Text;
            if (_fieldNames.Contains(name) && !_localNames.Contains(name))
            {
                // Check it's not already qualified (e.g. result._field)
                if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node)
                    return base.VisitIdentifierName(node); // already qualified

                // Replace with result.fieldName
                var resultAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("result"),
                    node.WithoutTrivia())
                    .WithTriviaFrom(node);
                return resultAccess;
            }
            return base.VisitIdentifierName(node);
        }
    }

    /// <summary>Replaces bare `return;` with `return result;` in void→struct migrated methods.</summary>
    private sealed class BareReturnRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
                return node.WithExpression(SyntaxFactory.IdentifierName("result"));
            return base.VisitReturnStatement(node);
        }
    }

    /// <summary>
    /// Rewrites bare calls to migrated methods (implicit this) to `result = result.Method(args)`.
    /// E.g., `Add(value);` → `result = result.Add(value);`
    /// </summary>
    private sealed class ImplicitCallRewriter : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _migratedMethodNames;
        private readonly string _currentMethodName;

        public ImplicitCallRewriter(HashSet<string> migratedMethodNames, string currentMethodName)
        {
            _migratedMethodNames = migratedMethodNames;
            _currentMethodName = currentMethodName;
        }

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            // Match bare invocation: Method(args); (no explicit receiver)
            if (node.Expression is InvocationExpressionSyntax invocation &&
                invocation.Expression is IdentifierNameSyntax methodName)
            {
                var name = methodName.Identifier.Text;
                // Don't rewrite recursive calls or non-migrated methods
                if (name != _currentMethodName && _migratedMethodNames.Contains(name))
                {
                    // Transform: Method(args) → result = result.Method(args)
                    var resultCall = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("result"),
                            methodName.WithoutTrivia()),
                        invocation.ArgumentList);

                    var assignment = SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName("result"),
                            resultCall))
                        .WithTriviaFrom(node);

                    return assignment;
                }
            }

            return base.VisitExpressionStatement(node);
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
