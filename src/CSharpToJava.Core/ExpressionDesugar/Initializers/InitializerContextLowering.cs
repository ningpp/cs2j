using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Result of classifying a site's position: the context kind plus whether a
/// statement sequence can actually be lifted there by the shared family
/// <see cref="ExpressionDesugarTempLifter"/> (design §2 decision 11/12).
/// </summary>
internal readonly record struct InitializerContextDecision(
    InitializerContextKind Kind,
    bool Liftable);

/// <summary>
/// C24 expression-position classification and statement-lowering gate
/// (design §4 IN-11 / decision 11). This is the C24 counterpart of C36's
/// minimal <c>CollectionContextLowering</c>; the shared parts are extracted up
/// to <c>Shared/</c> append-only when the family reviews it (design §3.2-4,
/// decision 4).
///
/// M1 supports exactly the main-path contexts (local/using declaration RHS,
/// return/yield return, throw, assignment RHS, expression statement and
/// expression-bodied members). Every other position — argument interleaving,
/// ternary branches, <c>??</c>, lambdas, nested initializers, field/property
/// initializers, attributes, default parameters, query clauses — is reported
/// as not liftable and the site is preserved without a diagnostic: those
/// statement forms land in M2 (CS2J0537/0538).
/// </summary>
internal static class InitializerContextLowering
{
    /// <summary>
    /// Classifies <paramref name="site"/>. <see cref="InitializerContextDecision.Liftable"/>
    /// is true only when the position is one of the M1 supported contexts AND an
    /// enclosing statement/arrow anchor exists for the shared TempLifter.
    /// </summary>
    public static InitializerContextDecision Classify(SyntaxNode site)
    {
        var child = (SyntaxNode)site;
        for (var parent = site.Parent; parent != null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                // M1 skip set / other rules' syntax faces (no diagnostic).
                case InitializerExpressionSyntax:
                case CollectionExpressionSyntax:
                case ArgumentSyntax:
                case AttributeArgumentSyntax:
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case AttributeSyntax:
                case ParameterSyntax:
                case QueryClauseSyntax:
                    return Unsupported();

                case ConditionalExpressionSyntax conditional
                    when child == conditional.WhenTrue || child == conditional.WhenFalse:
                    return Unsupported(InitializerContextKind.Ternary);

                case BinaryExpressionSyntax binary
                    when binary.IsKind(SyntaxKind.CoalesceExpression)
                         && child == binary.Right:
                    return Unsupported(InitializerContextKind.NullCoalescing);

                case EqualsValueClauseSyntax equalsValue:
                    return ClassifyDeclaration(equalsValue);

                case PropertyDeclarationSyntax property
                    when child == property.Initializer:
                    return Unsupported(InitializerContextKind.PropertyInitializer);

                case ReturnStatementSyntax:
                case YieldStatementSyntax:
                    return Supported(InitializerContextKind.Return);

                case ThrowStatementSyntax:
                case ThrowExpressionSyntax:
                    return Supported(InitializerContextKind.Throw);

                // Only a real assignment statement is a liftable RHS position; an
                // assignment that is an initializer item stays unsupported (the
                // enclosing InitializerExpression case is reached next).
                case AssignmentExpressionSyntax assignment
                    when child == assignment.Right
                         && assignment.Parent is ExpressionStatementSyntax:
                    return Supported(InitializerContextKind.Assignment);

                case ArrowExpressionClauseSyntax arrow when child == arrow.Expression:
                    // An expression-bodied lambda has no member block the shared
                    // lifter could expand (TempLifter returns the node unchanged).
                    return arrow.Parent is AnonymousFunctionExpressionSyntax
                        ? Unsupported(InitializerContextKind.LambdaTarget)
                        : Supported(InitializerContextKind.Return);

                case ExpressionStatementSyntax:
                    return Supported(InitializerContextKind.ExpressionStatement);
            }

            // Never climb past the nearest statement: reaching one without a
            // recognized value slot means the site is wrapped by an unsupported
            // construct (call argument list, nested expression, ...).
            if (parent is StatementSyntax)
                return Unsupported();

            // A lambda/local-function boundary between the site and an outer
            // statement changes evaluation timing; treat as unsupported (M2).
            if (parent is AnonymousFunctionExpressionSyntax
                or LocalFunctionStatementSyntax)
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
                    : InitializerContextKind.LocalDecl),
            UsingStatementSyntax => Supported(InitializerContextKind.Using),
            _ => Unsupported(),
        };
    }

    private static InitializerContextDecision Supported(InitializerContextKind kind)
        => new(kind, true);

    private static InitializerContextDecision Unsupported(
        InitializerContextKind kind = InitializerContextKind.Unknown)
        => new(kind, false);
}
