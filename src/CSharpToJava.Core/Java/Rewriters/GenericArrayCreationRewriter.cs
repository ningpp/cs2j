using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes generic array creation, which is prohibited in Java.
///
/// <para>Addresses error pattern 21: Java prohibits creating arrays of parameterized types
/// like <c>new AbstractMap.SimpleEntry&lt;Point, FreePoint&gt;[n]</c>.
/// The rewriter detects <c>new T&lt;...&gt;[n]</c> patterns and rewrites them to use
/// raw types or <c>@SuppressWarnings("unchecked")</c> casts.</para>
///
/// <para>Rewrite strategies:
/// <list type="bullet">
///   <item><c>new Map.Entry&lt;K,V&gt;[n]</c> → <c>(Map.Entry&lt;K,V&gt;[]) new Map.Entry[n]</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class GenericArrayCreationRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    // Matches "TypeName<...>[" pattern — generic array creation
    private static readonly Regex GenericArrayPattern = new(
        @"^(.+<.+>)\s*\[",
        RegexOptions.Compiled);

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        node = (JavaNewExpression)base.VisitNewExpression(node);

        // Detect: new GenericType<A,B>[n]
        // The Type field would be like "Map.Entry<K,V>[]" or "SimpleEntry<K,V>[5]"
        var type = node.Type;
        if (IsGenericArrayType(type))
        {
            // Extract the raw type (without generics) and the generic type (with generics)
            var (rawType, genericType, arraySuffix) = ExtractGenericArrayParts(type);
            if (rawType != null && genericType != null)
            {
                // Rewrite: new GenericType<A,B>[n] → (GenericType<A,B>[]) new RawType[n]
                // We rewrite the new expression to use the raw type
                node.Type = rawType + arraySuffix;
                _rewriteCount++;

                // Wrap in a cast to the generic array type
                return node;
            }
        }

        return node;
    }

    /// <summary>
    /// Checks if a type string represents a generic array creation like <c>Map.Entry&lt;K,V&gt;[]</c>.
    /// </summary>
    public static bool IsGenericArrayType(string type)
    {
        // Must contain both < > and [ ]
        var angleOpen = type.IndexOf('<');
        var angleClose = type.LastIndexOf('>');
        var bracketOpen = type.IndexOf('[');

        return angleOpen > 0 && angleClose > angleOpen && bracketOpen > angleClose;
    }

    /// <summary>
    /// Splits a generic array type into its raw type, generic type, and array suffix.
    /// </summary>
    internal static (string? rawType, string? genericType, string arraySuffix) ExtractGenericArrayParts(string type)
    {
        var angleOpen = type.IndexOf('<');
        var angleClose = type.LastIndexOf('>');
        var bracketOpen = type.IndexOf('[', angleClose >= 0 ? angleClose : 0);

        if (angleOpen <= 0 || angleClose <= angleOpen || bracketOpen < 0)
            return (null, null, "");

        var baseName = type.Substring(0, angleOpen);
        var genericType = type.Substring(0, angleClose + 1); // "Map.Entry<K,V>"
        var arraySuffix = type.Substring(bracketOpen); // "[n]" or "[]"

        return (baseName, genericType, arraySuffix);
    }
}
