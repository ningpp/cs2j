using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar;

namespace CSharpToJava.Core.ExpressionDesugar.Initializers;

/// <summary>
/// Syntax facts for C24: cheap dirty-gate triggers, the three residual node
/// kinds (design §2 decision 15), exclusion predicates (array / anonymous
/// object) and the shared explained-annotation pairing helper.
/// </summary>
internal static class InitializerSyntaxFacts
{
    /// <summary>
    /// Cheap syntax-only dirty gate (design §2 decision 1): an object creation
    /// carrying an initializer, or any target-typed <c>new()</c> (bare or with
    /// initializer). Array/collection expressions are owned by C36 and never
    /// trigger here.
    /// </summary>
    public static bool IsTriggerNode(SyntaxNode node) => node switch
    {
        ObjectCreationExpressionSyntax { Initializer: not null } => true,
        ImplicitObjectCreationExpressionSyntax => true,
        _ => false,
    };

    public static bool HasTriggerSyntax(CompilationUnitSyntax root)
        => root.DescendantNodes().Any(IsTriggerNode);

    /// <summary>
    /// True for the three node kinds the residual gate tracks (design §2
    /// decision 15): object initializer expression, collection initializer
    /// expression (including complex-element elements) and bare
    /// <c>new()</c>. Array initializer expressions and anonymous object
    /// creations are excluded (they are never owned by this rule).
    /// </summary>
    public static bool IsResidualNode(SyntaxNode node) => node switch
    {
        ImplicitObjectCreationExpressionSyntax => true,
        InitializerExpressionSyntax initializer => IsOwnedInitializer(initializer),
        _ => false,
    };

    /// <summary>True for the object/collection/complex initializer kinds owned by the rule.</summary>
    public static bool IsOwnedInitializer(InitializerExpressionSyntax initializer)
        => initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
           || initializer.IsKind(SyntaxKind.CollectionInitializerExpression)
           || initializer.IsKind(SyntaxKind.ComplexElementInitializerExpression);

    /// <summary>
    /// Exclusion predicate: array initializer expressions (<c>new int[] { … }</c>,
    /// <c>new[] { … }</c>) are emitted verbatim by the main converter and are
    /// never counted as C24 residuals.
    /// </summary>
    public static bool IsExcludedInitializer(SyntaxNode node)
        => node is InitializerExpressionSyntax initializer
           && initializer.IsKind(SyntaxKind.ArrayInitializerExpression);

    /// <summary>Exclusion predicate: anonymous object creations are owned by C21.</summary>
    public static bool IsExcludedCreation(SyntaxNode node)
        => node is AnonymousObjectCreationExpressionSyntax;

    /// <summary>True when the initializer item is a dictionary index assignment (<c>[k] = v</c>).</summary>
    public static bool IsIndexAssignment(AssignmentExpressionSyntax assignment)
        => assignment.Left is ImplicitElementAccessSyntax or ElementAccessExpressionSyntax;

    /// <summary>True when the initializer item is a complex element initializer (<c>{ a, b }</c>).</summary>
    public static bool IsComplexElement(ExpressionSyntax element)
        => element is InitializerExpressionSyntax initializer
           && initializer.IsKind(SyntaxKind.ComplexElementInitializerExpression);

    /// <summary>True when the expression is a nested creation carrying its own initializer.</summary>
    public static bool IsNestedCreation(ExpressionSyntax? expression)
        => expression switch
        {
            ObjectCreationExpressionSyntax { Initializer: not null } => true,
            ImplicitObjectCreationExpressionSyntax => true,
            _ => false,
        };

    /// <summary>
    /// True when <paramref name="node"/> is nested inside a C36-owned
    /// <see cref="CollectionExpressionSyntax"/>. The two syntax faces are
    /// disjoint (design §1.4); a creation-with-initializer used as a collection
    /// element belongs to C36's element emission, and rewriting it first would
    /// invalidate C36's original-node plan keys (rewriters run rule after rule
    /// on the shared tree), so M1 preserves such sites.
    /// </summary>
    public static bool IsInsideCollectionExpression(SyntaxNode node)
        => node.Ancestors().Any(static a => a is CollectionExpressionSyntax);

    /// <summary>True when <paramref name="node"/> itself or any ancestor carries the
    /// family explained annotation: a preserved site accounts for the whole
    /// subtree.
    /// </summary>
    public static bool IsExplained(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current.HasAnnotation(ExpressionDesugarSyntaxFacts.ExplainedAnnotation))
                return true;
            current = current.Parent;
        }

        return false;
    }
}