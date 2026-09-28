using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Analysis;

/// <summary>
/// Shared predicate for lambdas assigned to Expression&lt;TDelegate&gt; (expression trees).
/// Extracted from JavaKeywordSyntaxFacts so every pass uses one definition (catalog §4 risk 8).
/// </summary>
public static class ExpressionTreeFacts
{
    public static bool IsExpressionTreeLambda(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var typeInfo = model.GetTypeInfo(lambda);
        var convertedType = typeInfo.ConvertedType;
        if (convertedType == null) return false;
        var fullName = convertedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return fullName.StartsWith("global::System.Linq.Expressions.Expression<", StringComparison.Ordinal);
    }
}
