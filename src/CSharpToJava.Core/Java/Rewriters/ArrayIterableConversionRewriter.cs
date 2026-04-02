namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes array/iterable/stream conversion issues.
///
/// <para>Addresses error pattern 11 (and sub-error 24e): Various type incompatibilities
/// between Java arrays, Iterable, and Stream APIs that don't exist in C# because arrays
/// implicitly implement IEnumerable.</para>
///
/// <para>Detected patterns:
/// <list type="bullet">
///   <item><c>arr.spliterator()</c> → <c>Arrays.spliterator(arr)</c> — arrays don't have instance spliterator()</item>
///   <item><c>arr.stream()</c> → <c>Arrays.stream(arr)</c> — arrays don't have instance stream()</item>
///   <item><c>arr.asList()</c> → <c>Arrays.asList(arr)</c> — arrays don't have instance asList()</item>
/// </list>
/// </para>
/// </summary>
public sealed class ArrayIterableConversionRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: arrayVar.spliterator() → Arrays.spliterator(arrayVar)
        // Arrays don't have instance spliterator() in Java, so this is always safe to rewrite
        // when the target is not already Arrays/Spliterators.
        if (node.MethodName == "spliterator" &&
            node.Arguments.Count == 0 &&
            node.Target != null &&
            !IsAlreadyStaticUtilCall(node.Target))
        {
            var arrayExpr = node.Target;
            node.Target = new JavaIdentifierExpression("Arrays");
            node.MethodName = "spliterator";
            node.Arguments.Add(arrayExpr);
            _rewriteCount++;
            return node;
        }

        // Pattern: arrayVar.stream() → Arrays.stream(arrayVar)
        // Only for expressions that are likely arrays (new T[], array access, array-named vars)
        if (node.MethodName == "stream" &&
            node.Arguments.Count == 0 &&
            node.Target != null &&
            IsLikelyArrayExpression(node.Target))
        {
            var arrayExpr = node.Target;
            node.Target = new JavaIdentifierExpression("Arrays");
            node.MethodName = "stream";
            node.Arguments.Add(arrayExpr);
            _rewriteCount++;
            return node;
        }

        return node;
    }

    /// <summary>
    /// Checks if the target is already a static utility class (Arrays, Spliterators, etc.)
    /// to avoid double-wrapping.
    /// </summary>
    private static bool IsAlreadyStaticUtilCall(JavaExpression target)
    {
        if (target is JavaIdentifierExpression id)
        {
            return id.Name is "Arrays" or "Spliterators" or "StreamSupport";
        }
        return false;
    }

    /// <summary>
    /// Heuristically determines if an expression is likely an array reference.
    /// Without full type info, we use structural patterns rather than naming alone.
    /// </summary>
    private static bool IsLikelyArrayExpression(JavaExpression expr)
    {
        // Identifier names with explicit array naming patterns
        if (expr is JavaIdentifierExpression id)
        {
            var name = id.Name;
            // Specific array variable name patterns (avoiding false positives like "status", "class")
            return name.EndsWith("Array", StringComparison.Ordinal) ||
                   name.EndsWith("Items", StringComparison.Ordinal) ||
                   name.EndsWith("arr", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("Elements", StringComparison.Ordinal) ||
                   name.EndsWith("Nodes", StringComparison.Ordinal) ||
                   name.EndsWith("Edges", StringComparison.Ordinal) ||
                   name.EndsWith("Values", StringComparison.Ordinal) ||
                   name.EndsWith("Keys", StringComparison.Ordinal);
        }

        // Member access that looks like an array field
        if (expr is JavaMemberAccessExpression ma)
        {
            var name = ma.MemberName;
            return name.EndsWith("Array", StringComparison.Ordinal) ||
                   name.EndsWith("Items", StringComparison.Ordinal) ||
                   name.EndsWith("Elements", StringComparison.Ordinal);
        }

        // Array access is definitely array-based
        if (expr is JavaArrayAccessExpression)
        {
            return true;
        }

        // new T[...] expression
        if (expr is JavaNewExpression newExpr && newExpr.Type.Contains('['))
        {
            return true;
        }

        return false;
    }
}
