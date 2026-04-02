namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes incomplete Stopwatch API mappings.
///
/// <para>Addresses error pattern 17: The compat library's <c>StopwatchHelper</c> may be
/// missing some static members that C#'s <c>System.Diagnostics.Stopwatch</c> provides.
/// Specifically:
/// <list type="bullet">
///   <item><c>StopwatchHelper.Frequency</c> → <c>1_000_000_000L</c> (nanosecond precision)</item>
///   <item><c>StopwatchHelper.getTimestamp()</c> → <c>System.nanoTime()</c></item>
///   <item><c>StopwatchHelper.GetTimestamp()</c> → <c>System.nanoTime()</c></item>
/// </list>
/// </para>
///
/// <para>These rewrites ensure that even if the compat library doesn't define these members,
/// the generated Java code uses standard Java equivalents directly.</para>
/// </summary>
public sealed class StopwatchApiRewriter : JavaSyntaxRewriter
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

        // Pattern: StopwatchHelper.Frequency → 1_000_000_000L
        if (result is JavaMemberAccessExpression memberAccess &&
            memberAccess.MemberName is "Frequency" or "frequency" &&
            IsStopwatchTarget(memberAccess.Target))
        {
            _rewriteCount++;
            return new JavaLiteralExpression("1_000_000_000L");
        }

        return result;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: StopwatchHelper.getTimestamp() → System.nanoTime()
        // Pattern: StopwatchHelper.GetTimestamp() → System.nanoTime()
        if (node.MethodName is "getTimestamp" or "GetTimestamp" &&
            node.Arguments.Count == 0 &&
            node.Target != null &&
            IsStopwatchTarget(node.Target))
        {
            node.Target = new JavaIdentifierExpression("System");
            node.MethodName = "nanoTime";
            _rewriteCount++;
        }

        // Pattern: Stopwatch.isHighResolution() or Stopwatch.IsHighResolution → true
        // (Java's System.nanoTime() is always high-resolution)

        return node;
    }

    private static bool IsStopwatchTarget(JavaExpression target)
    {
        if (target is JavaIdentifierExpression id)
        {
            return id.Name is "StopwatchHelper" or "Stopwatch";
        }
        if (target is JavaMemberAccessExpression ma)
        {
            return ma.MemberName is "StopwatchHelper" or "Stopwatch";
        }
        return false;
    }
}
