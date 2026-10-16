using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.ExpressionDesugar.CollectionExpr;
using CSharpToJava.Core.ExpressionDesugar.Initializers;

namespace CSharpToJava.Core.ExpressionDesugar;

/// <summary>
/// File-level syntax profile produced by ONE shared pre-order walk
/// (<see cref="ExpressionDesugarTriggerScan"/>) and consumed by every rule's
/// cheap dirty gate. Each rule's <c>IsPotentiallyDirty</c> decision is a pure
/// function of these flags, so the family entry pays a single tree traversal
/// instead of one per rule (the four rules' gates used to traverse the whole
/// tree — up to five node walks plus a token walk — per file, which dominated
/// the pass on large clean files).
/// </summary>
internal readonly struct ExpressionDesugarSyntaxProfile
{
    /// <summary>At least one <see cref="InterpolatedStringExpressionSyntax"/>.</summary>
    public bool HasInterpolatedString { get; init; }

    /// <summary>
    /// At least one C24 trigger node: an object creation carrying an
    /// initializer, or a target-typed <c>new()</c>.
    /// </summary>
    public bool HasInitializerTrigger { get; init; }

    /// <summary>At least one <see cref="ConditionalAccessExpressionSyntax"/>.</summary>
    public bool HasConditionalAccess { get; init; }

    /// <summary>At least one C36 <see cref="CollectionExpressionSyntax"/>.</summary>
    public bool HasCollectionExpression { get; init; }

    /// <summary>
    /// CE-GUARD trigger syntax (interface static abstract/virtual members,
    /// generic attribute declarations/applications, <c>scoped</c> tokens).
    /// </summary>
    public bool HasGuardSyntax { get; init; }
}

/// <summary>
/// Single-pass syntax scanner backing the family dirty gate. Reproduces the
/// exact predicates the four individual rule gates used (C23 interpolation
/// string, C24 initializer trigger, C35 conditional access, C36 collection
/// expression / CE-GUARD) in one <c>DescendantNodes()</c> traversal; the
/// CE-GUARD <c>scoped</c> token walk runs only when the source text actually
/// contains "scoped" (a <see cref="Microsoft.CodeAnalysis.CSharp.SyntaxKind.ScopedKeyword"/>
/// token's text is exactly that literal, so the text check is a sound filter).
/// </summary>
internal static class ExpressionDesugarTriggerScan
{
    public static ExpressionDesugarSyntaxProfile Scan(
        CompilationUnitSyntax root,
        string sourceCode)
    {
        var hasInterpolatedString = false;
        var hasInitializerTrigger = false;
        var hasConditionalAccess = false;
        var hasCollectionExpression = false;
        var hasGuardSyntax = false;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case InterpolatedStringExpressionSyntax:
                    hasInterpolatedString = true;
                    break;
                case ConditionalAccessExpressionSyntax:
                    hasConditionalAccess = true;
                    break;
                case CollectionExpressionSyntax:
                    hasCollectionExpression = true;
                    break;
            }

            if (!hasInitializerTrigger && InitializerSyntaxFacts.IsTriggerNode(node))
                hasInitializerTrigger = true;

            if (!hasGuardSyntax && NewSyntaxGuardAnalyzer.IsGuardNode(node))
                hasGuardSyntax = true;

            // Every flag already satisfied: nothing left to look for.
            if (hasInterpolatedString
                && hasInitializerTrigger
                && hasConditionalAccess
                && hasCollectionExpression
                && hasGuardSyntax)
            {
                break;
            }
        }

        if (!hasGuardSyntax && sourceCode.Contains("scoped", StringComparison.Ordinal))
        {
            hasGuardSyntax = root.DescendantTokens()
                .Any(static token => token.IsKind(SyntaxKind.ScopedKeyword));
        }

        return new ExpressionDesugarSyntaxProfile
        {
            HasInterpolatedString = hasInterpolatedString,
            HasInitializerTrigger = hasInitializerTrigger,
            HasConditionalAccess = hasConditionalAccess,
            HasCollectionExpression = hasCollectionExpression,
            HasGuardSyntax = hasGuardSyntax,
        };
    }
}
