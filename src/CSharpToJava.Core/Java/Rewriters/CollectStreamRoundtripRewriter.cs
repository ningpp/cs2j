namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes redundant <c>.collect(...).stream()</c> round-trips in Stream chains.
///
/// <para>Addresses error pattern 15: The converter sometimes inserts unnecessary 
/// <c>.collect(Collectors.toList())</c> in the middle of a stream chain, followed immediately
/// by <c>.stream()</c> to continue streaming. This is inefficient and can cause type mismatches.</para>
///
/// <para>Detected patterns:
/// <list type="bullet">
///   <item><c>stream.collect(...).stream()</c> → <c>stream</c> (remove collect/stream round-trip)</item>
///   <item><c>expr.stream().collect(...).stream()</c> → <c>expr.stream()</c> (keep first stream)</item>
/// </list>
/// </para>
/// </summary>
public sealed class CollectStreamRoundtripRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        var result = base.VisitExpression(node);

        // Pattern: <expr>.collect(<collector>).stream()
        //     ->   <expr>
        //
        // When .stream() is called immediately after .collect(), the collect is redundant.
        // We remove both .collect() and .stream() to keep the Stream chain intact.
        if (result is JavaMethodCallExpression streamCall &&
            streamCall.MethodName == "stream" &&
            streamCall.Arguments.Count == 0 &&
            streamCall.Target is JavaMethodCallExpression collectCall &&
            collectCall.MethodName == "collect" &&
            collectCall.Target != null)
        {
            _rewriteCount++;
            return collectCall.Target;
        }

        return result;
    }
}
