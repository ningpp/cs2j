using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ReadOnlyStructMaker;

/// <summary>
/// Updates call sites of migrated void→struct methods:
/// - `s.Method(args);` → `s = s.Method(args);`
/// - `arr[i].Method(args);` → `arr[i] = arr[i].Method(args);`
/// - Return-this methods need no call-site update (semantically compatible).
/// </summary>
internal sealed class CallSiteUpdater : CSharpSyntaxRewriter
{
    private readonly HashSet<string> _migratedVoidMethodNames;

    public CallSiteUpdater(HashSet<string> migratedVoidMethodNames)
    {
        _migratedVoidMethodNames = migratedVoidMethodNames;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (node.Expression is InvocationExpressionSyntax invocation &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var methodName = memberAccess.Name.Identifier.Text;
            if (_migratedVoidMethodNames.Contains(methodName))
            {
                var receiver = memberAccess.Expression;

                // s.Method(args) → s = s.Method(args)
                var assignment = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    receiver.WithoutTrivia(),
                    invocation.WithoutTrivia())
                    .WithTriviaFrom(node.Expression);

                return node.WithExpression(assignment);
            }
        }

        return base.VisitExpressionStatement(node);
    }
}
