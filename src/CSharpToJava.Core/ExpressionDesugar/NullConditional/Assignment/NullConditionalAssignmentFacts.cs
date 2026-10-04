using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.ExpressionDesugar.NullConditional.Assignment;

/// <summary>
/// Syntax/semantic facts for the C51 assignment terminal (design §2/§4.1):
/// operator classification, target classification via IOperation, value-type
/// receiver detection, native-illegal shape predicates and the E03-damaged
/// orphan-binding probe.
/// </summary>
internal static class NullConditionalAssignmentFacts
{
    /// <summary>Classifies the assignment operator from its syntax kind.</summary>
    public static AssignmentOperatorKind ClassifyOperator(
        AssignmentExpressionSyntax assignment)
        => assignment.Kind() switch
        {
            SyntaxKind.SimpleAssignmentExpression => AssignmentOperatorKind.Simple,
            SyntaxKind.CoalesceAssignmentExpression => AssignmentOperatorKind.Coalesce,
            SyntaxKind.AddAssignmentExpression
                or SyntaxKind.SubtractAssignmentExpression
                or SyntaxKind.MultiplyAssignmentExpression
                or SyntaxKind.DivideAssignmentExpression
                or SyntaxKind.ModuloAssignmentExpression
                or SyntaxKind.AndAssignmentExpression
                or SyntaxKind.OrAssignmentExpression
                or SyntaxKind.ExclusiveOrAssignmentExpression
                or SyntaxKind.LeftShiftAssignmentExpression
                or SyntaxKind.RightShiftAssignmentExpression
                => AssignmentOperatorKind.Compound,
            _ => AssignmentOperatorKind.Unknown,
        };

    /// <summary>
    /// Bucket label for the per-operator counter: the operator token with the
    /// trailing <c>=</c> removed (<c>+=</c> → <c>+</c>).
    /// </summary>
    public static string OperatorLabel(AssignmentExpressionSyntax assignment)
        => assignment.OperatorToken.Text.TrimEnd('=');

    /// <summary>
    /// Classifies the LHS target shape. The truth source is the IOperation of
    /// the assignment terminal (design §2.2): event-assignment, field/property
    /// reference, ref-returning invocation — anything else (incl. invalid
    /// operations) is <see cref="AssignmentTargetShape.Unknown"/>.
    /// </summary>
    public static AssignmentTargetShape ClassifyTarget(
        AssignmentExpressionSyntax assignment,
        SemanticModel model,
        out bool isStatic)
    {
        isStatic = false;

        var operation = model.GetOperation(assignment);
        switch (operation)
        {
            case IEventAssignmentOperation eventAssignment:
                // Roslyn 5 types EventReference as IOperation; the event shape
                // is IEventReferenceOperation.
                if (eventAssignment.EventReference
                    is IEventReferenceOperation eventReference)
                {
                    isStatic = eventReference.Event.IsStatic;
                }
                return AssignmentTargetShape.Event;

            case IAssignmentOperation assignmentOperation:
            {
                var target = assignmentOperation.Target;
                switch (target)
                {
                    case IFieldReferenceOperation { Field: { } fieldSymbol }:
                        isStatic = fieldSymbol.IsStatic;
                        return AssignmentTargetShape.Member;

                    case IPropertyReferenceOperation { Property: { } propertySymbol }:
                        isStatic = propertySymbol.IsStatic;
                        // An indexer property renders through the index bucket.
                        return propertySymbol.IsIndexer
                            ? AssignmentTargetShape.Index
                            : AssignmentTargetShape.Member;

                    case IArrayElementReferenceOperation:
                        return AssignmentTargetShape.Index;

                    case IInvocationOperation invocation:
                        return invocation.TargetMethod is
                        {
                            RefKind: RefKind.Ref or RefKind.RefReadOnly,
                        }
                            ? AssignmentTargetShape.RefReturningMethod
                            : AssignmentTargetShape.Unknown;

                    default:
                        // Syntax cross-check: ElementBinding LHS with a missing
                        // IOperation still renders mechanically.
                        if (assignment.Left is ElementBindingExpressionSyntax)
                            return AssignmentTargetShape.Index;
                        return AssignmentTargetShape.Unknown;
                }
            }

            default:
                // No usable IOperation (e.g. some illegal shapes): fall back to
                // syntax; MemberBinding without a resolved target stays Unknown.
                if (assignment.Left is ElementBindingExpressionSyntax)
                    return AssignmentTargetShape.Index;
                return AssignmentTargetShape.Unknown;
        }
    }

    /// <summary>
    /// True when the outermost segment receiver is a value type. Reports
    /// whether it is a <see cref="System.Nullable{T}"/> and, when so, whether
    /// its underlying type is a record struct (the CS2J0562 make-readonly
    /// handoff domain).
    /// </summary>
    public static bool IsValueTypeReceiver(
        ChainPlan plan,
        out bool isNullable,
        out bool underlyingIsRecord)
    {
        isNullable = false;
        underlyingIsRecord = false;

        var type = plan.Segments[0].ReceiverType;
        if (type is not { IsValueType: true })
            return false;

        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType
               == SpecialType.System_Nullable_T)
        {
            isNullable = true;
            if (named.TypeArguments[0] is INamedTypeSymbol underlying)
                underlyingIsRecord = underlying.IsRecord;
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Native-illegal shape predicates (design F5/F8; CS2J0569 safety net).
    // ------------------------------------------------------------------

    /// <summary>
    /// True when a postfix/prefix unary operator (<c>++</c>/<c>--</c>) binds
    /// directly over the chain root: <c>a?.b++</c> / <c>--a?.b</c> (CS1059).
    /// </summary>
    public static bool IsUnaryWrapped(ConditionalAccessExpressionSyntax root)
    {
        var current = root.Parent;
        while (current is ParenthesizedExpressionSyntax)
            current = current.Parent;

        return current is PostfixUnaryExpressionSyntax
            or PrefixUnaryExpressionSyntax;
    }

    /// <summary>
    /// True when the chain root sits inside a tuple used as the LHS of an
    /// assignment (deconstruction target, e.g. <c>(a?.b, x) = (c, y)</c>).
    /// A tuple on the RHS is legal and does not match.
    /// </summary>
    public static bool IsDeconstructionLhs(ConditionalAccessExpressionSyntax root)
    {
        var tupleSeen = false;

        foreach (var ancestor in root.Ancestors())
        {
            switch (ancestor)
            {
                case ParenthesizedExpressionSyntax:
                    continue;

                case TupleExpressionSyntax:
                    tupleSeen = true;
                    continue;

                case AssignmentExpressionSyntax assignment when tupleSeen:
                    return assignment.Left.DescendantNodesAndSelf()
                        .OfType<TupleExpressionSyntax>().Any();

                default:
                    if (ancestor is StatementSyntax
                        or MemberDeclarationSyntax
                        or LambdaExpressionSyntax
                        or ArrowExpressionClauseSyntax)
                    {
                        return false;
                    }

                    continue;
            }
        }

        return false;
    }

    /// <summary>Strips parenthesized wrappers from an expression.</summary>
    public static ExpressionSyntax StripParens(ExpressionSyntax expression)
    {
        var current = expression;
        while (current is ParenthesizedExpressionSyntax parenthesized)
            current = parenthesized.Expression;
        return current;
    }

    // ------------------------------------------------------------------
    // E03-damaged shape probe (design A4 / §3.5).
    // ------------------------------------------------------------------

    /// <summary>
    /// True when the tree contains an orphan placeholder binding (a
    /// MemberBinding/ElementBinding without any ConditionalAccess ancestor).
    /// Legal source always nests bindings inside CAs; orphans are the damage
    /// an unguarded E03 produces (<c>var ___tN = .b;</c>, <c>.b = c;</c>).
    /// </summary>
    public static bool HasOrphanBindings(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(node =>
            node is MemberBindingExpressionSyntax
                or ElementBindingExpressionSyntax
            && !node.Ancestors().OfType<ConditionalAccessExpressionSyntax>().Any());
}
