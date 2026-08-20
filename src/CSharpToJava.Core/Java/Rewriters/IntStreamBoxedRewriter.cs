namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes IntStream/Stream type mismatches by inserting <c>.boxed()</c>
/// calls when IntStream methods are chained with operations expecting <c>Stream&lt;T&gt;</c>.
///
/// <para>Addresses error pattern 10: <c>Arrays.stream(int[])</c> returns <c>IntStream</c>
/// (primitive specialization). When <c>flatMap()</c> or <c>map()</c> is called with a lambda
/// that returns <c>Stream&lt;T&gt;</c> (not <c>IntStream</c>), Java requires <c>.boxed()</c>
/// to convert <c>IntStream</c> to <c>Stream&lt;Integer&gt;</c> first.</para>
///
/// <para>Detected patterns:
/// <list type="bullet">
///   <item><c>Arrays.stream(intArr).flatMap(...)</c> → <c>Arrays.stream(intArr).boxed().flatMap(...)</c></item>
///   <item><c>IntStream.range(...).mapToObj(...)</c> (already correct — no rewrite needed)</item>
///   <item><c>intArr.stream().flatMap(...)</c> → not an IntStream, no rewrite</item>
/// </list>
/// </para>
/// </summary>
public sealed class IntStreamBoxedRewriter : JavaSyntaxRewriter
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

        // Pattern: <intStreamExpr>.flatMap(lambda) → <intStreamExpr>.boxed().flatMap(lambda)
        // Pattern: <intStreamExpr>.map(lambda) → <intStreamExpr>.boxed().map(lambda)
        //
        // We detect "intStreamExpr" by checking if the receiver chain starts with
        // Arrays.stream() or IntStream.range()/rangeClosed().
        if (IsStreamOperationNeedingBoxed(node.MethodName) &&
            node.Target != null &&
            IsLikelyIntStream(node.Target))
        {
            // Insert .boxed() between the IntStream source and the chained operation
            var boxedCall = new JavaMethodCallExpression
            {
                Target = node.Target,
                MethodName = "boxed",
            };
            node.Target = boxedCall;
            _rewriteCount++;
        }

        return node;
    }

    /// <summary>
    /// Stream operations that require <c>Stream&lt;T&gt;</c> receiver (not <c>IntStream</c>).
    /// Note: IntStream has its own reduce() method, so we don't include it here.
    /// </summary>
    private static bool IsStreamOperationNeedingBoxed(string methodName)
        => methodName is "flatMap" or "map" or "collect" or "toList" or "toArray";

    /// <summary>
    /// Java primitive array type keywords that produce primitive streams
    /// (IntStream/LongStream/DoubleStream) when passed to Arrays.stream().
    /// </summary>
    private static readonly HashSet<string> PrimitiveArrayKeywords = new()
    {
        "int", "long", "double", "byte", "short", "char", "float", "boolean"
    };

    /// <summary>
    /// Heuristically determines if an expression is likely an IntStream.
    /// </summary>
    private static bool IsLikelyIntStream(JavaExpression expr)
    {
        // Arrays.stream(int[]) returns IntStream
        if (expr is JavaMethodCallExpression call)
        {
            // Arrays.stream(x) — only IntStream if the argument is a primitive array
            if (call.MethodName == "stream" &&
                call.Target is JavaIdentifierExpression { Name: "Arrays" })
            {
                // Arrays.stream(T[]) for reference T returns Stream<T>, not IntStream.
                // Only Arrays.stream(primitive[]) returns a primitive stream.
                // Check if the argument text looks like a primitive array creation
                // or a primitive array variable — we cannot know the variable type
                // at the IR level, so we conservatively only return true when the
                // argument is explicitly a primitive array literal (new int[]{...}).
                if (call.Arguments.Count == 1 &&
                    IsPrimitiveArrayArgument(call.Arguments[0]))
                {
                    return true;
                }
                // If we cannot determine the argument type, do NOT assume IntStream.
                // This prevents false positives on Arrays.stream(String[]).
                return false;
            }

            // IntStream.range(...) / IntStream.rangeClosed(...)
            if (call.MethodName is "range" or "rangeClosed" &&
                call.Target is JavaIdentifierExpression { Name: "IntStream" })
            {
                return true;
            }

            // IntStream.of(...)
            if (call.MethodName == "of" &&
                call.Target is JavaIdentifierExpression { Name: "IntStream" })
            {
                return true;
            }
        }

        // Direct IntStream identifier
        if (expr is JavaIdentifierExpression { Name: "IntStream" })
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if an argument expression is a primitive-type array by inspecting
    /// its rendered text. Recognizes <c>new int[]{...}</c>, <c>new byte[]{...}</c>,
    /// and also primitive array variables when the raw code text contains a
    /// primitive type keyword followed by brackets (e.g. <c>intArr</c> does not
    /// match — we only match explicit <c>new &lt;primitive&gt;[</c> patterns or
    /// JavaNewExpression with a primitive element type).
    /// </summary>
    private static bool IsPrimitiveArrayArgument(JavaExpression arg)
    {
        // JavaNewExpression: new T[] { ... } — check the Type property
        if (arg is JavaNewExpression newExpr && !string.IsNullOrEmpty(newExpr.Type))
        {
            return IsPrimitiveArrayType(newExpr.Type);
        }

        // Fall back to text inspection for JavaRawExpression and other node types
        var text = arg.ToInlineString().Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        // Match "new int[", "new long[", etc.
        foreach (var prim in PrimitiveArrayKeywords)
        {
            if (text.StartsWith($"new {prim}[", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a type string like "int[]" or "int[][]" represents a primitive array.
    /// </summary>
    private static bool IsPrimitiveArrayType(string type)
    {
        var t = type.Trim();
        // Strip array brackets
        while (t.EndsWith("[]", StringComparison.Ordinal))
            t = t[..^2].Trim();
        return PrimitiveArrayKeywords.Contains(t);
    }
}
