using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NullCoalesceRewriter;

/// <summary>
/// Single-pass CSharpSyntaxRewriter. Expressions are rewritten bottom-up;
/// VisitBlock inserts every guard statement (temp declaration for ?? or the
/// 'if (lhs == null) lhs = rhs;' guard for value-context ??=) registered
/// while visiting a statement directly before that rewritten statement.
/// '??=' used in a statement position is turned into an if-statement; used in
/// a value position it is hoisted plus replaced by the lvalue, or lowered to
/// an inline ternary when no statement slot exists.
/// </summary>
internal sealed class NullCoalesceSyntaxRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel? _semanticModel;
    private readonly NullCoalesceStatistics _stats;
    private readonly List<NullCoalesceDiagnostic> _diagnostics;
    private readonly NullCoalesceOptions _options;

    // Guard statements (temp declarations and if-guards) collected while a
    // statement subtree is being visited. VisitBlock drains the guards created
    // by each statement directly before the rewritten statement, so insertion
    // never depends on post-rewrite node identity. Guards produced inside a
    // nested block visit are marked consumed so outer blocks do not insert
    // them a second time.
    internal List<PendingGuard> PendingGuards { get; } = new();

    private int _tempCounter;
    private int _nestedDepth;

    internal sealed class PendingGuard
    {
        public PendingGuard(StatementSyntax statement) => Statement = statement;
        public StatementSyntax Statement { get; }
        public bool Consumed { get; set; }
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(RewriteStatementList(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(RewriteStatementList(node.Statements)));

    /// <summary>
    /// Visits each statement in order and splices in the guards it produced.
    /// Shared by block bodies and switch sections (the two plain statement
    /// containers); block-bodied lambdas/local functions are separate blocks
    /// and consume their own guards.
    /// </summary>
    private List<StatementSyntax> RewriteStatementList(SyntaxList<StatementSyntax> statements)
    {
        var newStatements = new List<StatementSyntax>();
        foreach (var stmt in statements)
        {
            var pendingCountBeforeVisit = PendingGuards.Count;
            var visited = (StatementSyntax?)Visit(stmt) ?? stmt;

            // Drain every guard registered while visiting THIS statement.
            // Guards registered inside a nested block/section are already
            // consumed by that container's own visit and skipped here.
            for (var i = pendingCountBeforeVisit; i < PendingGuards.Count; i++)
            {
                var guard = PendingGuards[i];
                if (guard.Consumed)
                {
                    continue;
                }

                guard.Consumed = true;
                newStatements.Add(guard.Statement);
            }

            newStatements.Add(visited);
        }

        return newStatements;
    }

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

        // Derive type from left operand. The hoisted temp is declared with 'var',
        // so the symbol is only consulted to decide whether the operand is a
        // Nullable<T>, whose true branch must read the unwrapped .Value. An
        // unresolved type — a declaration living in an assembly outside the pass
        // compilation's reference set, e.g. MSTest's TestContext in a test project
        // — therefore still rewrites: the plain reference-typed temp is the only
        // provable shape, and leaving the operator behind would trip the strict
        // residual gate and abort the whole conversion.
        var typeSymbol = _semanticModel?.GetTypeInfo(node.Left).Type;
        var leftIsNullableValueType = typeSymbol is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

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
        // When the left operand is a nullable VALUE type (T?), the temp is T?
        // and the conditional's true branch must read ___tN.Value: C# types the
        // whole conditional as T? while the branch is just ___tN (CS0266 when
        // the original expression's type is the underlying T). Nullable
        // reference types need no such unwrap.
        var tempRef = SyntaxFactory.IdentifierName(tempName);
        ExpressionSyntax trueBranch = tempRef;
        if (leftIsNullableValueType)
        {
            trueBranch = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                tempRef.WithoutTrivia(),
                SyntaxFactory.IdentifierName("Value"));
        }

        var rewritten = SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.ConditionalExpression(
                SyntaxFactory.BinaryExpression(
                    SyntaxKind.NotEqualsExpression,
                    tempRef,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                trueBranch,
                right));

        // A statement slot is required to hoist the temp declaration (a field
        // initializer or expression-bodied member provides none).
        if (FindAnchorStatement(node) == null)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot insert temp variable declaration for ?? expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        PendingGuards.Add(new PendingGuard(tempDecl));
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

        // A statement-form 'lhs ??= rhs;' is intercepted in
        // VisitExpressionStatement. Reaching here means the '??=' is used in a
        // VALUE context (argument, arrow body, initializer, operand, ...): the
        // result must stay an expression and the conditional assignment has to
        // be hoisted to (or expressed without) a statement slot.

        // Guard (C51 hard prerequisite, design §3.5/A4): a '??=' whose left
        // is a MemberBinding/ElementBinding lives inside a ConditionalAccess
        // (e.g. a?.b ??= c). Rewriting it here would emit a bare binding left
        // — an uncompilable tree. The C51 assignment rule owns that lowering.
        if (node.Left is MemberBindingExpressionSyntax
            or ElementBindingExpressionSyntax)
        {
            _stats.SkippedUnsupported++;
            return node;
        }

        // Process right side first (may contain nested ?? / ??=)
        var visitedRight = (ExpressionSyntax?)Visit(node.Right);
        var right = visitedRight ?? node.Right;
        var left = node.Left;

        if (left is IdentifierNameSyntax identifier)
        {
            _stats.NullCoalescingAssignRewritten++;

            // Statement slot available: hoist the conditional assignment before
            // the enclosing statement and replace the expression with the
            // identifier, which holds the post-assignment result.
            if (FindAnchorStatement(node) is not null)
            {
                PendingGuards.Add(new PendingGuard(BuildIfAssignsNull(identifier, right)));
                return identifier;
            }

            // No statement slot (expression-bodied member / field initializer):
            // keep the value semantics inline as a ternary —
            // (id == null ? (id = rhs) : id)
            return SyntaxFactory.ParenthesizedExpression(BuildInlineTernary(identifier, right));
        }

        // Member access / indexer lvalue: resolving its type lets the rewrite
        // skip unbound nodes instead of producing broken output.
        var typeSymbol = _semanticModel?.GetTypeInfo(left).Type;
        if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
        {
            _diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Warning,
                $"Cannot resolve type of left operand in ??= expression; skipping rewrite."));
            _stats.SkippedUnsupported++;
            return node;
        }

        if (FindAnchorStatement(node) is not null)
        {
            _stats.NullCoalescingAssignRewritten++;

            // A member/indexer lvalue whose evaluation has side effects (e.g.
            // 'a[i++] ??= x' or 'Get().P ??= x') must evaluate its receiver and
            // index arguments exactly once. The plain 'if (lhs == null) lhs = rhs'
            // guard clones the lvalue and would run those side effects twice/three
            // times, so materialize it into temps first.
            if (LvalueMayHaveSideEffects(left))
            {
                var (value, guardStmts) = BuildStabilizedCoalesceAssign(left, right);
                foreach (var guardStmt in guardStmts)
                    PendingGuards.Add(new PendingGuard(guardStmt));
                return value;
            }

            // Hoist the guard and read the (possibly freshly assigned) lvalue.
            // The receiver/index expressions are evaluated like in the
            // statement form of the pass.
            PendingGuards.Add(new PendingGuard(BuildIfAssignsNull(left, right)));
            return left;
        }

        // Side-effecting lvalue in a statement-free expression context cannot
        // be lowered inline without changing evaluation semantics; keep it for
        // the residual gate instead of emitting invalid code.
        _diagnostics.Add(new NullCoalesceDiagnostic(
            NullCoalesceSeverity.Warning,
            $"Cannot hoist member/indexer ??= without a statement slot; skipping rewrite."));
        _stats.SkippedUnsupported++;
        return node;
    }

    /// <summary>
    /// Rewrites the statement form 'lhs ??= rhs;' into the guard statement
    /// 'if (lhs == null) { lhs = rhs; }'. Value-context occurrences never
    /// reach here (see VisitAssignmentExpression).
    /// </summary>
    private SyntaxNode RewriteCoalesceAssignStatement(
        ExpressionStatementSyntax exprStmt,
        AssignmentExpressionSyntax assign)
    {
        // Process right side first (may contain nested ?? / ??=)
        var visitedRight = (ExpressionSyntax?)Visit(assign.Right);
        var right = visitedRight ?? assign.Right;
        var left = assign.Left;

        if (left is not IdentifierNameSyntax)
        {
            var typeSymbol = _semanticModel?.GetTypeInfo(left).Type;
            if (typeSymbol == null || typeSymbol is IErrorTypeSymbol)
            {
                _diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Warning,
                    $"Cannot resolve type of left operand in ??= expression; skipping rewrite."));
                _stats.SkippedUnsupported++;
                return exprStmt;
            }
        }

        _stats.NullCoalescingAssignRewritten++;

        // Same single-evaluation requirement as the value form: a side-effecting
        // member/indexer target (e.g. 'a[i++] ??= x;') must not be cloned into
        // both the null test and the assignment.
        if (left is not IdentifierNameSyntax && LvalueMayHaveSideEffects(left))
        {
            var (_, guardStmts) = BuildStabilizedCoalesceAssign(left, right);
            return Block(guardStmts);
        }

        return BuildIfAssignsNull(left, right);
    }

    /// <summary>Builds 'if (lhs == null) { lhs = rhs; }'.</summary>
    private static IfStatementSyntax BuildIfAssignsNull(ExpressionSyntax lhs, ExpressionSyntax rhs)
        => SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                lhs.WithoutTrivia(),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.Block(
                SyntaxFactory.ExpressionStatement(
                    SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        lhs.WithoutTrivia(),
                        rhs))));

    /// <summary>
    /// Builds 'lhs == null ? (lhs = rhs) : lhs' — the value-yielding inline
    /// form of 'lhs ??= rhs' for statement-free expression contexts. Each lhs
    /// occurrence is a fresh clone since a syntax node cannot be parented
    /// twice; the Formatter pass normalizes spacing afterwards.
    /// </summary>
    private static ConditionalExpressionSyntax BuildInlineTernary(
        ExpressionSyntax lhs,
        ExpressionSyntax rhs)
        => SyntaxFactory.ConditionalExpression(
            SyntaxFactory.BinaryExpression(
                SyntaxKind.EqualsExpression,
                lhs.WithoutTrivia(),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)),
            SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    lhs.WithoutTrivia(),
                    rhs)),
            lhs.WithoutTrivia());

    private string GenerateTempName()
    {
        _tempCounter++;
        return $"___t{_tempCounter}";
    }

    /// <summary>
    /// True when evaluating the assignment target expression itself can run
    /// observable side effects. C# evaluates the target of '??=' exactly once;
    /// cloning such an lvalue into a null test plus a write-back (plus the
    /// value-context read) would re-run those effects. Covers ++/-- operands,
    /// invocations in the receiver/arguments, nested assignments and await.
    /// </summary>
    private static bool LvalueMayHaveSideEffects(ExpressionSyntax lvalue)
    {
        foreach (var node in lvalue.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case PostfixUnaryExpressionSyntax p
                    when p.IsKind(SyntaxKind.PostIncrementExpression)
                        || p.IsKind(SyntaxKind.PostDecrementExpression):
                case PrefixUnaryExpressionSyntax pre
                    when pre.IsKind(SyntaxKind.PreIncrementExpression)
                        || pre.IsKind(SyntaxKind.PreDecrementExpression):
                case InvocationExpressionSyntax:
                case AssignmentExpressionSyntax:
                case AwaitExpressionSyntax:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Lowers a side-effecting member/indexer 'lhs ??= rhs' while preserving the
    /// C# single-evaluation rule. The receiver chain and every index argument are
    /// materialized into temps, the current value is read once into another temp,
    /// and only that temp/null test/write-back remains:
    /// <code>
    /// var __i = i++;                 // (each side-effecting sub-expression once)
    /// var __v = a[__i];              // one read of the stable target
    /// if (__v == null) { __v = rhs; a[__i] = __v; }
    /// </code>
    /// Returns the temp expression yielding the post-assignment value (for the
    /// value-context replacement) and the ordered guard statements.
    /// </summary>
    private (ExpressionSyntax Value, List<StatementSyntax> Statements)
        BuildStabilizedCoalesceAssign(ExpressionSyntax lvalue, ExpressionSyntax rhs)
    {
        var statements = new List<StatementSyntax>();
        var stableTarget = StabilizeLvalue(lvalue, statements);

        var valueName = GenerateTempName();
        var valueRef = IdentifierName(valueName);
        statements.Add(LocalVarDeclaration(valueName, stableTarget));

        var writeBack = ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            stableTarget,
            valueRef.WithoutTrivia()));
        var assignRhs = ExpressionStatement(AssignmentExpression(
            SyntaxKind.SimpleAssignmentExpression,
            valueRef.WithoutTrivia(),
            rhs));
        statements.Add(IfStatement(
            BinaryExpression(
                SyntaxKind.EqualsExpression,
                valueRef.WithoutTrivia(),
                LiteralExpression(SyntaxKind.NullLiteralExpression)),
            Block(assignRhs, writeBack)));

        return (valueRef, statements);
    }

    /// <summary>
    /// Rebuilds a member/indexer assignment target so it is free of side effects
    /// (composed only of identifiers/temps), hoisting each effectful receiver or
    /// index argument into its own temp declaration.
    /// </summary>
    private ExpressionSyntax StabilizeLvalue(
        ExpressionSyntax lvalue, List<StatementSyntax> statements)
    {
        switch (lvalue)
        {
            case ElementAccessExpressionSyntax elementAccess:
            {
                var receiver = StabilizeSubExpression(elementAccess.Expression, statements);
                var arguments = elementAccess.ArgumentList.Arguments
                    .Select(argument => argument.RefKindKeyword.IsKind(SyntaxKind.None)
                        ? argument.WithExpression(
                            StabilizeSubExpression(argument.Expression, statements))
                        : argument)
                    .ToList();
                return ElementAccessExpression(
                    receiver,
                    BracketedArgumentList(SeparatedList(arguments)));
            }

            case MemberAccessExpressionSyntax memberAccess:
            {
                var receiver = StabilizeSubExpression(memberAccess.Expression, statements);
                return MemberAccessExpression(
                    memberAccess.Kind(), receiver, memberAccess.Name.WithoutTrivia());
            }

            default:
                return StabilizeSubExpression(lvalue, statements);
        }
    }

    /// <summary>
    /// Keeps side-effect-free references (locals/parameters/this/literals) as-is;
    /// any other sub-expression is hoisted into a 'var __tN = expr;' temp so it
    /// evaluates exactly once.
    /// </summary>
    private ExpressionSyntax StabilizeSubExpression(
        ExpressionSyntax expression, List<StatementSyntax> statements)
    {
        if (expression is IdentifierNameSyntax
            or LiteralExpressionSyntax
            or ThisExpressionSyntax)
        {
            return expression.WithoutTrivia();
        }

        var name = GenerateTempName();
        statements.Add(LocalVarDeclaration(name, expression.WithoutTrivia()));
        return IdentifierName(name);
    }

    private static LocalDeclarationStatementSyntax LocalVarDeclaration(
        string name, ExpressionSyntax initializer)
        => LocalDeclarationStatement(
            VariableDeclaration(
                IdentifierName("var"),
                SingletonSeparatedList(VariableDeclarator(
                    Identifier(name), null, EqualsValueClause(initializer)))));

    /// <summary>
    /// Walks up the parent chain to find the nearest enclosing StatementSyntax
    /// that can host a hoisted guard. Returns null when no such slot exists:
    /// expression-bodied members/initializers, or an expression-bodied
    /// lambda/local-function boundary (hoisting into the enclosing method
    /// would change WHEN the guarded code executes). A block-bodied lambda or
    /// local function still resolves to its own inner statement, which is
    /// found before the boundary node on the way up.
    /// </summary>
    private static StatementSyntax? FindAnchorStatement(SyntaxNode node)
    {
        SyntaxNode? current = node.Parent;
        while (current != null)
        {
            if (current is LambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax
                or LocalFunctionStatementSyntax)
            {
                return null;
            }

            if (current is StatementSyntax stmt)
            {
                return stmt;
            }

            current = current.Parent;
        }
        return null;
    }
}
