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
    /// Heuristically determines if an expression is likely an IntStream.
    /// </summary>
    private static bool IsLikelyIntStream(JavaExpression expr)
    {
        // Arrays.stream(int[]) returns IntStream
        if (expr is JavaMethodCallExpression call)
        {
            // Arrays.stream(x) — likely IntStream if target is Arrays
            if (call.MethodName == "stream" &&
                call.Target is JavaIdentifierExpression { Name: "Arrays" })
            {
                return true;
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
}
