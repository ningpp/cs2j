using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.DelegatePreprocessor;

/// <summary>
/// Single-pass CSharpSyntaxRewriter. Rewrites delegate + / - to
/// CsPrelude.Combine / CsPrelude.Remove, and anonymous methods to lambdas.
/// Only queries SemanticModel on original (pre-rewrite) nodes.
/// </summary>
internal sealed class DelegateSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly DelegatePreprocessorStatistics _stats;
    private readonly List<DelegatePreprocessorDiagnostic> _diagnostics;
    private readonly DelegatePreprocessorOptions _options;

    public bool Changed { get; private set; }

    public DelegateSyntaxRewriter(
        SemanticModel? semanticModel,
        DelegatePreprocessorOptions options,
        DelegatePreprocessorStatistics stats,
        List<DelegatePreprocessorDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // Visit children first (bottom-up). CSharpSyntaxRewriter default
        // visits children first, but we call base to be explicit.
        var visited = base.VisitBinaryExpression(node);
        if (visited is not BinaryExpressionSyntax binary)
            return visited;

        // Only handle + and -
        if (!binary.IsKind(SyntaxKind.AddExpression) && !binary.IsKind(SyntaxKind.SubtractExpression))
            return binary;

        // Need semantic model to determine if operands are delegate types
        if (_semanticModel == null)
            return binary;

        // Check if either operand is a delegate type (use ORIGINAL node for semantic query)
        var leftType = _semanticModel.GetTypeInfo(node.Left).Type;
        var rightType = _semanticModel.GetTypeInfo(node.Right).Type;

        bool leftIsDelegate = IsDelegateType(leftType);
        bool rightIsDelegate = IsDelegateType(rightType);

        if (!leftIsDelegate && !rightIsDelegate)
            return binary; // numeric + / -, not our concern

        // Flatten left-nested AddExpression chains for Combine
        if (binary.IsKind(SyntaxKind.AddExpression))
        {
            var operands = new List<ExpressionSyntax>();
            CollectAddOperands(binary, operands);

            if (operands.Count >= 3)
            {
                // Use params overload: Combine(a, b, c)
                _stats.CombineRewritten++;
                Changed = true;
                return CreateCombineCall(operands.ToArray())
                    .WithTriviaFrom(node);
            }
        }

        // Two-operand case
        var left = (ExpressionSyntax)binary.Left;
        var right = (ExpressionSyntax)binary.Right;

        if (binary.IsKind(SyntaxKind.AddExpression))
        {
            _stats.CombineRewritten++;
            Changed = true;
            return CreateCombineCall(left, right)
                .WithTriviaFrom(node);
        }
        else
        {
            _stats.RemoveRewritten++;
            Changed = true;
            return CreateRemoveCall(left, right)
                .WithTriviaFrom(node);
        }
    }

    private static bool IsDelegateType(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.TypeKind == TypeKind.Delegate;
    }

    private void CollectAddOperands(BinaryExpressionSyntax node, List<ExpressionSyntax> operands)
    {
        if (node.Left is BinaryExpressionSyntax leftBinary
            && leftBinary.IsKind(SyntaxKind.AddExpression))
        {
            CollectAddOperands(leftBinary, operands);
        }
        else
        {
            operands.Add(node.Left);
        }
        operands.Add(node.Right);
    }

    private static ExpressionSyntax CreateCombineCall(params ExpressionSyntax[] args)
    {
        var argList = SyntaxFactory.SeparatedList(
            args.Select((a, i) => SyntaxFactory.Argument(NormalizeArgTrivia(a, i, args.Length))));
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("CsPrelude"),
                SyntaxFactory.IdentifierName("Combine")),
            SyntaxFactory.ArgumentList(argList));
    }

    private static ExpressionSyntax CreateRemoveCall(ExpressionSyntax a, ExpressionSyntax b)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("CsPrelude"),
                SyntaxFactory.IdentifierName("Remove")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(new[]
                {
                    SyntaxFactory.Argument(NormalizeArgTrivia(a, 0, 2)),
                    SyntaxFactory.Argument(NormalizeArgTrivia(b, 1, 2))
                })));
    }

    /// <summary>
    /// Strip trailing trivia from the first argument and leading trivia
    /// from the last argument to avoid stray spaces in the generated call.
    /// </summary>
    private static ExpressionSyntax NormalizeArgTrivia(ExpressionSyntax expr, int index, int count)
    {
        if (index == 0)
            return expr.WithTrailingTrivia();
        if (index == count - 1)
            return expr.WithLeadingTrivia();
        return expr.WithTrailingTrivia().WithLeadingTrivia();
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        var visited = base.VisitAssignmentExpression(node);
        if (visited is not AssignmentExpressionSyntax assignment)
            return visited;

        if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression)
            && !assignment.IsKind(SyntaxKind.SubtractAssignmentExpression))
            return assignment;

        if (_semanticModel == null)
            return assignment;

        // Check if left operand is a delegate type (query original node)
        var leftType = _semanticModel.GetTypeInfo(node.Left).Type;
        if (!IsDelegateType(leftType))
            return assignment; // numeric += / -=, not our concern

        // Check if left is an event — skip events
        var leftSymbol = _semanticModel.GetSymbolInfo(node.Left).Symbol;
        if (leftSymbol is IEventSymbol)
            return assignment;

        // Stable reference check: only rewrite if left is a simple identifier
        // or a member access without side effects in the receiver
        if (!IsStableLeftValue(node.Left))
        {
            _stats.SkippedUnsupported++;
            _diagnostics.Add(new DelegatePreprocessorDiagnostic(
                DelegatePreprocessorSeverity.Warning,
                "Compound assignment on non-simple left value is not supported; skipping rewrite."));
            return assignment;
        }

        var left = (ExpressionSyntax)assignment.Left;
        var right = (ExpressionSyntax)assignment.Right;

        ExpressionSyntax newRight;
        if (assignment.IsKind(SyntaxKind.AddAssignmentExpression))
        {
            _stats.CompoundAssignRewritten++;
            newRight = CreateCombineCall(left, right);
        }
        else
        {
            _stats.CompoundAssignRewritten++;
            newRight = CreateRemoveCall(left, right);
        }

        Changed = true;
        return SyntaxFactory.AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            left,
            SyntaxFactory.Token(SyntaxKind.EqualsToken)
                .WithTriviaFrom(assignment.OperatorToken),
            newRight)
            .WithTriviaFrom(node);
    }

    /// <summary>
    /// A left value is "stable" (safe to duplicate) if it is an identifier
    /// or a member access whose receiver contains no invocations or
    /// prefix/postfix unary operators.
    /// </summary>
    private static bool IsStableLeftValue(ExpressionSyntax left)
    {
        // Strip parentheses
        while (left is ParenthesizedExpressionSyntax p)
            left = p.Expression;

        if (left is IdentifierNameSyntax)
            return true;

        if (left is MemberAccessExpressionSyntax member)
        {
            // Check receiver for side effects
            var receiver = member.Expression;
            while (receiver is MemberAccessExpressionSyntax ma)
                receiver = ma.Expression;
            // Receiver must not contain invocations or ++/--
            return !receiver.DescendantNodesAndSelf()
                .Any(n => n is InvocationExpressionSyntax
                       || n is PrefixUnaryExpressionSyntax
                       || n is PostfixUnaryExpressionSyntax);
        }

        return false;
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
        => base.VisitAnonymousMethodExpression(node);
}
