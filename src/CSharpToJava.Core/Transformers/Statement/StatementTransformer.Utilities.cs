using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Abstractions;
using CSharpToJava.Core.Comments;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java;
using CSharpToJava.Core.Transformers;
using CSharpToJava.Core.Transformers.Expression;
using CSharpToJava.Core.Transformers.Expression.Utilities;

namespace CSharpToJava.Core.Transformers.Statement;

public partial class StatementTransformer
{
    /// <summary>
    /// Returns true when <paramref name="javaType"/> is a Java collection/list type whose
    /// constructor or assignment slot requires a <c>Collection</c>-compatible value.
    /// Excludes <c>Iterable</c> because that is already replaced by <c>var</c> earlier.
    /// </summary>
    private static bool IsJavaCollectionOrListType(string javaType)
    {
        // An array of collections (e.g. ArrayList<Integer>[]) is NOT itself a collection type.
        if (javaType.TrimEnd().EndsWith("[]"))
            return false;
        var bare = javaType.Contains('<') ? javaType[..javaType.IndexOf('<')] : javaType;
        return bare is "List" or "Collection" or "ArrayList" or "HashSet" or "TreeSet"
            or "LinkedList" or "LinkedHashSet" or "ArrayDeque" or "Stack" or "Vector"
            or "Set" or "Deque" or "Queue";
    }

    /// <summary>
    /// Checks if an expression ends with a balanced .collect(...) call.
    /// Unlike a simple regex, this correctly handles nested parentheses so
    /// expressions like ".collect(groupingBy(...)).entrySet()" are NOT detected.
    /// </summary>
    private static bool EndsWithCollectCall(string expr)
    {
        if (!expr.EndsWith(")"))
            return false;
        // Walk backward to find the matching '(' for the final ')'
        int depth = 0;
        int i = expr.Length - 1;
        for (; i >= 0; i--)
        {
            if (expr[i] == ')') depth++;
            else if (expr[i] == '(') depth--;
            if (depth == 0) break;
        }
        // i now points to the matching '(' — check if preceded by ".collect"
        const string collectSuffix = ".collect";
        return i >= collectSuffix.Length
            && expr.AsSpan((i - collectSuffix.Length), collectSuffix.Length).SequenceEqual(collectSuffix.AsSpan());
    }

    /// <summary>
    /// Checks whether an expression contains a lambda or method reference.
    /// Used to detect ternary expressions that Java var can't infer.
    /// </summary>
    private static bool ContainsLambdaOrMethodRef(ExpressionSyntax expr)
    {
        if (expr is ParenthesizedExpressionSyntax paren)
            return ContainsLambdaOrMethodRef(paren.Expression);
        if (expr is CastExpressionSyntax cast)
            // A cast in a ternary branch (e.g., (DelegateType)MethodGroup) signals delegate conversion.
            // Return true conservatively — forces explicit type instead of var, which is always safe.
            return true;
        return expr is SimpleLambdaExpressionSyntax
            or ParenthesizedLambdaExpressionSyntax
            or AnonymousMethodExpressionSyntax
            || expr is MemberAccessExpressionSyntax;
    }

    /// <summary>
    /// Returns a Java default-value expression for a C# value type, or null for reference types.
    /// Used to generate getOrDefault() calls for TryGetValue on value-type dictionary values.
    /// </summary>
    private static string? GetValueTypeDefault(ITypeSymbol? typeSymbol, string javaTypeName)
    {
        if (typeSymbol == null || !typeSymbol.IsValueType)
            return null;

        return javaTypeName switch
        {
            "int" => "0",
            "long" => "0L",
            "short" => "(short)0",
            "byte" => "(byte)0",
            "float" => "0.0f",
            "double" => "0.0",
            "boolean" => "false",
            "char" => "'\\0'",
            _ => $"new {javaTypeName}()"
        };
    }

    /// <summary>
    /// Converts a string to PascalCase by capitalizing the first letter.
    /// Used for generating JavaBean-compliant getter method names.
    /// </summary>
    private static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToUpper(name[0]) + name.Substring(1);
    }
}
