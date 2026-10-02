using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Result of classifying a site's position: the context kind, whether a
/// statement sequence can actually be lifted there by the shared family
/// <see cref="ExpressionDesugarTempLifter"/> (design §2 decision 11/12), and
/// the owner node for conditional/coalesce/argument lowering.
/// </summary>
internal readonly record struct InitializerContextDecision(
    InitializerContextKind Kind,
    bool Liftable,
    SyntaxNode? Owner);

/// <summary>
/// C24 expression-position classification and statement-lowering gate
/// (design §4 IN-11 / decision 11). This is the C24 counterpart of C36's
/// minimal <c>CollectionContextLowering</c>; the shared parts are extracted up
/// to <c>Shared/</c> append-only when the family reviews it (design §3.2-4,
/// decision 4).
///
/// M2 supports: local/using declaration RHS, return/yield return, throw,
/// assignment RHS, expression statements, expression-bodied members, <c>?:</c>
/// branches (synthesized if/else with a definite-assignment temp, guard NOT
/// hoisted), <c>??</c> right operand and argument positions (preceding
/// side-effecting arguments hoisted left-to-right). Anchor-free positions
/// (field/property initializers, attributes, default parameters, query
/// clauses) map to CS2J0538; v1-unsupported statement positions (constructor
/// chain arguments, lambda/method-group inferred targets) map to CS2J0537.
/// </summary>
internal static class InitializerContextLowering
{
    /// <summary>Classifies <paramref name="site"/> (kind, liftability, owner).</summary>
    public static InitializerContextDecision Classify(SyntaxNode site)
    {
        var decision = ClassifyCore(site);
        if (!decision.Liftable)
            return decision;

        // A statement/arrow anchor must exist and no lambda/local-function
        // boundary may separate the site from it (evaluation timing).
        var anchor = ExpressionDesugarTempLifter.FindAnchor(site);
        if (anchor == null)
            return Unsupported(decision.Kind);

        if (IsSeparatedByFunctionBoundary(site, anchor))
            return Unsupported(InitializerContextKind.LambdaTarget);

        return decision;
    }

    private static InitializerContextDecision ClassifyCore(SyntaxNode site)
    {
        var child = (SyntaxNode)site;
        for (var parent = site.Parent; parent != null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                // Constructor-chain arguments (`: this(...)` / `: base(...)`):
                // v1 does not statement-lower here.
                case ConstructorInitializerSyntax:
                    return Unsupported(InitializerContextKind.ConstructorChain);

                // Anchor-free contexts (design §2 decision 12): CS2J0538.
                case AttributeSyntax:
                case AttributeArgumentSyntax:
                    return Unsupported(InitializerContextKind.AttributeArgument);

                case ParameterSyntax:
                    return Unsupported(InitializerContextKind.DefaultParameter);

                case QueryClauseSyntax:
                    return Unsupported(InitializerContextKind.QueryClause);

                case FieldDeclarationSyntax:
                case EventFieldDeclarationSyntax:
                    return Unsupported(InitializerContextKind.FieldInitializer);

                case PropertyDeclarationSyntax property when child == property.Initializer:
                    return Unsupported(InitializerContextKind.PropertyInitializer);

                // An array literal is not a nested-initializer boundary: the
                // site is still liftable at the enclosing statement.
                case InitializerExpressionSyntax initializer
                    when initializer.IsKind(SyntaxKind.ArrayInitializerExpression):
                    break;

                // Nested object/collection initializer boundary would have been
                // consumed by the parent plan; reaching one here means an
                // unhandled shape -> anchor-free.
                case InitializerExpressionSyntax:
                    return Unsupported();

                // Lambda / local-function boundaries; an expression-bodied
                // lambda's own arrow has no member block to expand.
                case AnonymousFunctionExpressionSyntax:
                    return Unsupported(InitializerContextKind.LambdaTarget);

                case LocalFunctionStatementSyntax:
                    return Unsupported(InitializerContextKind.LambdaTarget);

                case ArrowExpressionClauseSyntax arrow when child == arrow.Expression:
                    return arrow.Parent is AnonymousFunctionExpressionSyntax
                        ? Unsupported(InitializerContextKind.LambdaTarget)
                        : Supported(InitializerContextKind.Return, arrow);

                // `?:` branch: context-sensitive lowering (guard kept).
                case ConditionalExpressionSyntax conditional
                    when child == conditional.WhenTrue || child == conditional.WhenFalse:
                    return Supported(InitializerContextKind.Ternary, conditional);

                // `??` right operand.
                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.CoalesceExpression)
                         && child == binary.Right:
                    return Supported(InitializerContextKind.NullCoalescing, binary);

                // Argument position (interleaving handled by the analyzer). A
                // constructor-chain argument is v1-unsupported (CS2J0537).
                case ArgumentSyntax argument
                    when argument.Parent is ArgumentListSyntax list:
                    return list.Parent is ConstructorInitializerSyntax
                        ? Unsupported(InitializerContextKind.ConstructorChain)
                        : Supported(InitializerContextKind.Argument, list);

                case EqualsValueClauseSyntax equalsValue:
                    return ClassifyDeclaration(equalsValue);

                case ReturnStatementSyntax:
                case YieldStatementSyntax:
                    return Supported(InitializerContextKind.Return, null);

                case ThrowStatementSyntax:
                case ThrowExpressionSyntax:
                    return Supported(InitializerContextKind.Throw, null);

                // Only an assignment statement's RHS is a liftable position.
                case AssignmentExpressionSyntax assignment
                    when child == assignment.Right
                         && assignment.Parent is ExpressionStatementSyntax:
                    return Supported(InitializerContextKind.Assignment, null);

                case ExpressionStatementSyntax:
                    return Supported(InitializerContextKind.ExpressionStatement, null);
            }

            if (parent is StatementSyntax)
                return Unsupported();
        }

        return Unsupported();
    }

    /// <summary>
    /// Classifies an <c>= value</c> clause by its declaration owner. Only
    /// local/using declarations are liftable; field/property/event initializers
    /// and <c>for</c> declarations are anchor-free (design §2 decision 12).
    /// </summary>
    private static InitializerContextDecision ClassifyDeclaration(
        EqualsValueClauseSyntax equalsValue)
    {
        if (equalsValue.Parent is not VariableDeclaratorSyntax declarator
            || declarator.Parent is not VariableDeclarationSyntax declaration)
        {
            return Unsupported();
        }

        return declaration.Parent switch
        {
            LocalDeclarationStatementSyntax local
                => Supported(local.UsingKeyword != default
                    ? InitializerContextKind.Using
                    : InitializerContextKind.LocalDecl, null),
            UsingStatementSyntax => Supported(InitializerContextKind.Using, null),
            _ => Unsupported(),
        };
    }

    /// <summary>
    /// Owner chain (outermost..innermost) of <c>?:</c>/<c>??</c> nodes that
    /// enclose <paramref name="site"/> through branch/right-operand edges. Used
    /// to allocate one definite-assignment result temp per owner.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> OwnerChain(SyntaxNode site)
    {
        var chain = new List<SyntaxNode>();
        var child = (SyntaxNode)site;
        for (var parent = site.Parent; parent != null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case ConditionalExpressionSyntax conditional
                    when child == conditional.WhenTrue || child == conditional.WhenFalse:
                    chain.Add(conditional);
                    break;

                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.CoalesceExpression)
                         && child == binary.Right:
                    chain.Add(binary);
                    break;

                default:
                    // Stop at the first non-owner boundary.
                    if (parent is StatementSyntax
                        or ArrowExpressionClauseSyntax
                        or ArgumentSyntax
                        or EqualsValueClauseSyntax)
                    {
                        chain.Reverse();
                        return chain;
                    }

                    // Any other node type ends the chain (a plain subexpression).
                    chain.Reverse();
                    return chain;
            }

            if (parent is StatementSyntax)
                break;
        }

        chain.Reverse();
        return chain;
    }

    private static bool IsSeparatedByFunctionBoundary(SyntaxNode site, SyntaxNode anchor)
    {
        var current = site.Parent;
        while (current != null && current != anchor)
        {
            if (current is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static InitializerContextDecision Supported(InitializerContextKind kind, SyntaxNode? owner)
        => new(kind, true, owner);

    private static InitializerContextDecision Unsupported(
        InitializerContextKind kind = InitializerContextKind.Unknown)
        => new(kind, false, null);
}
