using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Two-pass CSharpSyntaxRewriter. Pass 1 (this class) rewrites ?? and ??=
/// bottom-up, collecting temp-variable declarations. Pass 2
/// (NullCoalesceStatementLifter) inserts the collected declarations
/// before their anchor statements.
/// </summary>
internal sealed class NullCoalesceSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly NullCoalesceStatistics _stats;
    private readonly List<NullCoalesceDiagnostic> _diagnostics;
    private readonly NullCoalesceOptions _options;

    // Pending temp-variable declarations collected during Pass 1.
    // Each entry: (declaration statement, anchor statement to insert before, order)
    internal List<PendingDeclaration> PendingDeclarations { get; } = new();

    private int _tempCounter;
    private int _nestedDepth;

    internal sealed record PendingDeclaration(
        StatementSyntax Declaration,
        StatementSyntax AnchorStatement,
        int Order);

    public NullCoalesceSyntaxRewriter(
        SemanticModel? semanticModel,
        NullCoalesceOptions options,
        NullCoalesceStatistics stats,
        List<NullCoalesceDiagnostic> diagnostics)
    {
        _semanticModel = semanticModel;
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.CoalesceExpression))
            return base.VisitBinaryExpression(node);

        // Bottom-up: process children first (inner ?? before outer)
        var visitedLeft = (ExpressionSyntax?)Visit(node.Left);
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var left = visitedLeft ?? node.Left;
        var right = visitedRight ?? node.Right;

        _nestedDepth++;
        if (_nestedDepth > 1)
            _stats.NestedRewrites++;
        _nestedDepth--;

        // Skip if left operand is null literal (meaningless but legal C#)
        if (left is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression))
            return node;

        // Derive type from left operand
        var typeSymbol = _semanticModel?.GetTypeInfo(node.Left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ?? expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        var tempName = GenerateTempName();

        // Build: var ___tN = <left>;
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName),
                        null,
                        SyntaxFactory.EqualsValueClause(left)))));

        // Build: (___tN != null) ? ___tN : <right>
        var tempRef = SyntaxFactory.IdentifierName(tempName);
        var rewritten = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.ConditionalExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    tempRef,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                tempRef,
                right));

        // Find anchor statement (nearest enclosing statement)
        var anchor = FindAnchorStatement(node);

        if (anchor != null)
        {
            PendingDeclarations.Add(new PendingDeclaration(tempDecl, anchor, PendingDeclarations.Count));
        }
        else
        {
            // Cannot insert declaration (e.g. field initializer, lambda expression body)
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot insert temp variable declaration for ?? expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        _stats.NullCoalescingRewritten++;
        _stats.TempVariablesGenerated++;
        return rewritten;
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // ??= appears as an ExpressionStatement in statement context.
        // We need to replace the entire ExpressionStatement with an IfStatement.
        if (node.Expression is AssignmentExpressionSyntax assign
            && assign.IsKind(SyntaxKind.CoalesceAssignmentExpression))
        {
            return RewriteCoalesceAssignStatement(node, assign);
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.CoalesceAssignmentExpression))
            return base.VisitAssignmentExpression(node);

        // Process right side first (may contain nested ??)
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;
        var left = node.Left;

        // Check if left is a simple identifier (local variable / parameter)
        if (left is IdentifierNameSyntax identifier)
        {
            // Simple identifier: no temp variable needed
            // Rewrite: a ??= expr  →  if (a == null) { a = expr; }
            var ifStmt = SyntaxFactory.IfStatement(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    identifier,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            identifier,
                            right))));

            _stats.NullCoalescingAssignRewritten++;
            return ifStmt;
        }

        // Member access / indexer: left may have side effects — use temp variable
        var typeSymbol = _semanticModel?.GetTypeInfo(left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        var tempName = GenerateTempName();

        // Build: var ___tN = <left>;
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName),
                        null,
                        SyntaxFactory.EqualsValueClause(left)))));

        // Build: if (___tN == null) { <left> = <right>; }
        var ifStmt2 = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(tempName),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        left,
                        right))));

        // The IfStatement replaces the ExpressionStatement containing the ??=.
        // The temp variable declaration needs to be inserted before the anchor.
        var anchor = FindAnchorStatement(node);
        if (anchor != null)
        {
            PendingDeclarations.Add(new PendingDeclaration(tempDecl, anchor, PendingDeclarations.Count));
        }
        else
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot insert temp variable declaration for ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        _stats.NullCoalescingAssignRewritten++;
        _stats.TempVariablesGenerated++;
        return ifStmt2;
    }

    /// <summary>
    /// Rewrites a ??= statement (ExpressionStatement containing CoalesceAssignmentExpression)
    /// into an IfStatement. For simple identifiers: if (a == null) { a = expr; }
    /// For member/indexer: temp var + if (___tN == null) { left = expr; }
    /// </summary>
    private SyntaxNode RewriteCoalesceAssignStatement(
        ExpressionStatementSyntax exprStmt,
        AssignmentExpressionSyntax assign)
    {
        // Process right side first (may contain nested ??)
        var visitedRight = (ExpressionSyntax?)Visit(assign.Right);
        var right = visitedRight ?? assign.Right;
        var left = assign.Left;

        // Check if left is a simple identifier (local variable / parameter)
        if (left is IdentifierNameSyntax identifier)
        {
            // Simple identifier: no temp variable needed
            // Rewrite: a ??= expr  →  if (a == null) { a = expr; }
            var ifStmt = SyntaxFactory.IfStatement(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    identifier,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                SyntaxFactory.Block(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            identifier,
                            right))));

            _stats.NullCoalescingAssignRewritten++;
            return ifStmt;
        }

        // Member access / indexer: left may have side effects — use temp variable
        var typeSymbol = _semanticModel?.GetTypeInfo(left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return exprStmt;
        }

        var tempName = GenerateTempName();

        // Build: var ___tN = <left>;
        var tempDecl = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(
                        SyntaxFactory.Identifier(tempName),
                        null,
                        SyntaxFactory.EqualsValueClause(left)))));

        // Build: if (___tN == null) { <left> = <right>; }
        var ifStmt2 = SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                SyntaxFactory.IdentifierName(tempName),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        left,
                        right))));

        // The temp variable declaration needs to be inserted before this ExpressionStatement.
        // The ExpressionStatement is the anchor.
        PendingDeclarations.Add(new PendingDeclaration(
            tempDecl, exprStmt, PendingDeclarations.Count));

        _stats.NullCoalescingAssignRewritten++;
        _stats.TempVariablesGenerated++;
        return ifStmt2;
    }

    private string GenerateTempName()
    {
        _tempCounter++;
        return $"___t{_tempCounter}";
    }

    /// <summary>
    /// Walks up the parent chain to find the nearest enclosing StatementSyntax.
    /// Returns null if no statement ancestor exists (e.g. field initializer).
    /// </summary>
    private StatementSyntax? FindAnchorStatement(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is StatementSyntax stmt)
                return stmt;
            current = current.Parent;
        }
        return null;
    }
}
