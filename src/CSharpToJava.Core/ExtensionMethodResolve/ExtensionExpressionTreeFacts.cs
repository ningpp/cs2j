using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ExtensionMethodResolve;

/// <summary>
/// Minimal facts for "is this invocation located inside an
/// <c>Expression&lt;...&gt;</c> lambda?" (EM-EXPRTREE, design §4.1).
/// If C02's shared <c>Analysis/ExpressionTreeFacts</c> lands first this file
/// merges into it instead of keeping a second copy.
///
/// M0 shell: conservatively reports false — no rules consume the answer yet.
/// </summary>
internal static class ExtensionExpressionTreeFacts
{
    /// <summary>True when <paramref name="node"/> is inside an expression-tree lambda.</summary>
    public static bool IsInExpressionTreeLambda(SyntaxNode? node, SemanticModel model)
    {
        // M2: walk ancestors, detect lambdas whose converted type is an
        // Expression<T> (the enclosing argument has an Expression<T> parameter).
        return false;
    }
}
