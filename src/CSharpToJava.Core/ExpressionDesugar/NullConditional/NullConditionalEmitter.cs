using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional;

/// <summary>
/// Pure rendering for C35 (design §4.3): placeholder-binding substitution,
/// null-check ternaries (incl. the <c>(T?)</c> preserving cast), segment
/// declarations/guarded prefetches and nested if blocks. Every helper is
/// functional: same inputs give equivalent output, no state.
/// </summary>
internal static class NullConditionalEmitter
{
    // ------------------------------------------------------------------
    // Placeholder binding substitution.
    // ------------------------------------------------------------------

    /// <summary>
    /// Replaces the terminal/segment expression's leftmost placeholder
    /// binding with accesses on <paramref name="receiver"/>: a
    /// <see cref="MemberBindingExpressionSyntax"/> becomes
    /// <c>receiver.Name</c>; an <see cref="ElementBindingExpressionSyntax"/>
    /// becomes <c>receiver[args]</c>.
    /// </summary>
    public static ExpressionSyntax ReplaceRootBinding(
        ExpressionSyntax bindingRootedExpression,
        ExpressionSyntax receiver)
    {
        var binding = NullConditionalSyntaxFacts.FindRootBinding(
            bindingRootedExpression);
        if (binding == null)
            return bindingRootedExpression;

        var replacement = binding switch
        {
            MemberBindingExpressionSyntax memberBinding
                => (ExpressionSyntax)SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    receiver,
                    memberBinding.Name),
            ElementBindingExpressionSyntax elementBinding
                => SyntaxFactory.ElementAccessExpression(
                    receiver,
                    elementBinding.ArgumentList),
            _ => receiver,
        };

        return (ExpressionSyntax)new BindingNodeSubstitutor(binding, replacement)
            .Visit(bindingRootedExpression)!;
    }

    /// <summary>Replaces exactly one node (the leftmost binding), then passes through.</summary>
    private sealed class BindingNodeSubstitutor : CSharpSyntaxRewriter
    {
        private readonly SyntaxNode _target;
        private readonly SyntaxNode _replacement;
        private bool _done;

        public BindingNodeSubstitutor(SyntaxNode target, SyntaxNode replacement)
        {
            _target = target;
            _replacement = replacement;
        }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (!_done && node == _target)
            {
                _done = true;
                return _replacement;
            }

            return base.Visit(node);
        }
    }

    // ------------------------------------------------------------------
    // Value consumption (single segment: Inline / TempTernary).
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the single-segment value consumption:
    /// <c>receiver != null ? terminal-on-receiver : null</c>, with the
    /// <c>(T?)</c> cast when the terminal lifts a value type. When the
    /// receiver itself is a <see cref="System.Nullable{T}"/> value type its
    /// members are only reachable through the underlying type, so the binding
    /// receiver is rendered as <c>((T)receiver)</c>.
    /// </summary>
    /// <param name="terminal">Visited terminal expression (binding-rooted).</param>
    /// <param name="receiverInCondition">Expression compared against null and used as the binding receiver.</param>
    /// <param name="receiverType">Static type of <paramref name="receiverInCondition"/>.</param>
    public static ExpressionSyntax BuildSingleSegmentTernary(
        ExpressionSyntax terminal,
        ExpressionSyntax receiverInCondition,
        ITypeSymbol? receiverType,
        ChainPlan plan)
    {
        var bindingReceiver = BindingReceiver(receiverInCondition, receiverType);
        var terminalAccess = ReplaceRootBinding(terminal, bindingReceiver);

        ExpressionSyntax whenTrue = plan.CastToNullable
            ? SyntaxFactory.CastExpression(plan.NullableCastType!, terminalAccess)
            : terminalAccess;
        ExpressionSyntax whenFalse = BuildNullBranch(plan);

        var condition = SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            receiverInCondition,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

        return SyntaxFactory.ConditionalExpression(
            condition, whenTrue, whenFalse);
    }

    /// <summary>The null literal (with the <c>(T?)</c> cast when lifting).</summary>
    public static ExpressionSyntax BuildNullBranch(ChainPlan plan)
    {
        ExpressionSyntax nullLiteral =
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression);
        return plan.CastToNullable
            ? SyntaxFactory.CastExpression(plan.NullableCastType!, nullLiteral)
            : nullLiteral;
    }

    // ------------------------------------------------------------------
    // Multi-segment structures (NC-2 guarded value / NC-8 nested void).
    // ------------------------------------------------------------------

    /// <summary>
    /// Prefix statements shared by every multi-segment lowering:
    /// segment-0 declaration, typed null initializers and the nested
    /// guarded-prefetch if chain.
    /// </summary>
    /// <param name="visitedCaByOriginal">Original CA nodes -> their visited counterparts (carries already-rewritten independent chains).</param>
    /// <param name="innermostStatements">Statements placed in the deepest guard (void terminal; empty for value).</param>
    public static List<StatementSyntax> BuildMultiSegmentPrefix(
        ChainPlan plan,
        IReadOnlyDictionary<ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax> visitedCaByOriginal,
        IReadOnlyList<StatementSyntax> innermostStatements)
    {
        var n = plan.Segments.Count;
        var statements = new List<StatementSyntax>
        {
            // var __ed_s0 = R0;
            ExpressionDesugarTempLifter.DeclareVar(
                plan.Segments[0].TempName,
                visitedCaByOriginal[plan.Segments[0].Ca].Expression),
        };

        if (n > 1)
        {
            // Every segment temp is declared at method scope up front;
            // only the assignments live inside the guard chain.
            for (var k = 1; k < n; k++)
            {
                statements.Add(DeclareNull(
                    plan.Segments[k].TempName,
                    SegmentNullableTypeSyntax(plan.Segments[k].ReceiverType)));
            }

            statements.Add(BuildPrefetchIf(plan, visitedCaByOriginal, 1, innermostStatements));
        }
        else if (innermostStatements.Count > 0)
        {
            // Single segment: if (__ed_s0 != null) { terminal }.
            statements.Add(SyntaxFactory.IfStatement(
                NotNullCheck(SyntaxFactory.IdentifierName(plan.Segments[0].TempName)),
                SyntaxFactory.Block(innermostStatements)));
        }

        return statements;
    }

    /// <summary>
    /// Recursively builds the guarded prefetch for segment k:
    /// <c>if (__ed_s{k-1} != null) { __ed_s{k} = Rk; deeper }</c>.
    /// </summary>
    private static IfStatementSyntax BuildPrefetchIf(
        ChainPlan plan,
        IReadOnlyDictionary<ConditionalAccessExpressionSyntax, ConditionalAccessExpressionSyntax> visitedCaByOriginal,
        int k,
        IReadOnlyList<StatementSyntax> innermostStatements)
    {
        var n = plan.Segments.Count;
        var segment = plan.Segments[k];
        var previousReceiver = BindingReceiver(
            SyntaxFactory.IdentifierName(plan.Segments[k - 1].TempName),
            plan.Segments[k - 1].ReceiverType);
        var receiver = ReplaceRootBinding(
            visitedCaByOriginal[segment.Ca].Expression,
            previousReceiver);

        var body = new List<StatementSyntax>
        {
            // __ed_s{k} = Rk;
            SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.IdentifierName(segment.TempName),
                    receiver)),
        };

        if (k < n - 1)
        {
            body.Add(BuildPrefetchIf(
                plan, visitedCaByOriginal, k + 1, innermostStatements));
        }
        else if (innermostStatements.Count > 0)
        {
            // Deepest segment: guard the terminal itself against a null s_k.
            body.Add(SyntaxFactory.IfStatement(
                NotNullCheck(SyntaxFactory.IdentifierName(segment.TempName)),
                SyntaxFactory.Block(innermostStatements)));
        }

        return SyntaxFactory.IfStatement(
            NotNullCheck(SyntaxFactory.IdentifierName(
                plan.Segments[k - 1].TempName)),
            SyntaxFactory.Block(body));
    }

    /// <summary>
    /// Builds the nested value consumption:
    /// <c>__ed_s0 != null ? (__ed_s1 != null ? terminal : null) : null</c>.
    /// The <c>(T?)</c> preserving cast wraps only the innermost terminal.
    /// </summary>
    public static ExpressionSyntax BuildNestedTernary(
        ChainPlan plan, ExpressionSyntax visitedTerminal)
        // Outer parentheses are mandatory: the replacement site may be the
        // left operand of an enclosing ?? (E03 leaves statement-anchored ??
        // intact), and without parens the coalesce would bind to the last
        // null branch instead of the whole chain.
        => SyntaxFactory.ParenthesizedExpression(
            BuildNestedTernaryLevel(plan, visitedTerminal, 0));

    private static ExpressionSyntax BuildNestedTernaryLevel(
        ChainPlan plan, ExpressionSyntax visitedTerminal, int k)
    {
        var receiver = SyntaxFactory.IdentifierName(plan.Segments[k].TempName);

        // Every level is guarded; at the deepest level the true branch is
        // the terminal itself (with the preserving cast), otherwise nest.
        ExpressionSyntax whenTrue;
        if (k == plan.Segments.Count - 1)
        {
            var terminal = ReplaceRootBinding(
                visitedTerminal, BindingReceiver(receiver, plan.Segments[k].ReceiverType));
            whenTrue = plan.CastToNullable
                ? SyntaxFactory.CastExpression(plan.NullableCastType!, terminal)
                : terminal;
        }
        else
        {
            whenTrue = BuildNestedTernaryLevel(plan, visitedTerminal, k + 1);
        }

        return SyntaxFactory.ConditionalExpression(
            NotNullCheck(receiver),
            whenTrue,
            BuildNullBranch(plan));
    }

    /// <summary>
    /// Builds the void terminal statement (e.g. <c>__ed_sN.M(args);</c>)
    /// executed inside the deepest guard.
    /// </summary>
    public static StatementSyntax BuildVoidTerminalStatement(
        ExpressionSyntax visitedTerminal,
        string deepestTempName,
        ITypeSymbol? deepestReceiverType)
        => SyntaxFactory.ExpressionStatement(
            ReplaceRootBinding(
                visitedTerminal,
                BindingReceiver(
                    SyntaxFactory.IdentifierName(deepestTempName),
                    deepestReceiverType)));

    // ------------------------------------------------------------------
    // Small node helpers.
    // ------------------------------------------------------------------

    /// <summary>
    /// Underlying type when <paramref name="type"/> is
    /// <see cref="System.Nullable{T}"/>, otherwise null.
    /// </summary>
    public static ITypeSymbol? NullableUnderlying(ITypeSymbol? type)
    {
        if (type is INamedTypeSymbol named
            && named.IsValueType
            && named.OriginalDefinition.ToDisplayString() == "System.Nullable<T>")
        {
            return named.TypeArguments[0];
        }

        return null;
    }

    /// <summary>
    /// The receiver expression used for a binding access. When its static
    /// type is <see cref="System.Nullable{T}"/>, members are unreachable
    /// directly; renders a parenthesized unwrap cast <c>((T)receiver)</c>
    /// which is safe on the guarded (non-null) path.
    /// </summary>
    private static ExpressionSyntax BindingReceiver(
        ExpressionSyntax receiver, ITypeSymbol? receiverType)
    {
        var underlying = NullableUnderlying(receiverType);
        if (underlying == null)
            return receiver;

        return SyntaxFactory.ParenthesizedExpression(
            SyntaxFactory.CastExpression(
                SyntaxFactory.ParseTypeName(underlying.ToDisplayString()),
                receiver));
    }

    /// <summary><c>receiver != null</c>.</summary>
    private static BinaryExpressionSyntax NotNullCheck(ExpressionSyntax receiver)
        => SyntaxFactory.BinaryExpression(
            SyntaxKind.NotEqualsExpression,
            receiver,
            SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));

    /// <summary><c>T? name = null;</c> (the T? / T shape follows the symbol nullability).</summary>
    private static LocalDeclarationStatementSyntax DeclareNull(
        string name, TypeSyntax typeSyntax)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(typeSyntax)
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(name)
                            .WithInitializer(
                                SyntaxFactory.EqualsValueClause(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.NullLiteralExpression))))));

    /// <summary>
    /// Type syntax for the segment's null-initialised temp: uses <c>T?</c>
    /// unless the symbol is already <see cref="Nullable{T}"/> or an annotated
    /// reference type.
    /// </summary>
    private static TypeSyntax SegmentNullableTypeSyntax(ITypeSymbol? type)
    {
        if (type == null)
            return SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword));

        var parsed = SyntaxFactory.ParseTypeName(type.ToDisplayString());

        var alreadyNullableValueType = type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        var alreadyAnnotatedReference = type.IsReferenceType
            && type.NullableAnnotation == NullableAnnotation.Annotated;

        return alreadyNullableValueType || alreadyAnnotatedReference
            ? parsed
            : SyntaxFactory.NullableType(parsed);
    }
}
