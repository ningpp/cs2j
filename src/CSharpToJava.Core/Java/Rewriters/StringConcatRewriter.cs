namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that replaces <c>String.Concat()</c> / <c>System.String.Concat()</c> calls
/// with <c>StringHelper.concat()</c>.
///
/// <para>C#'s <c>String.Concat()</c> is a static method that concatenates arguments
/// (including null-safe handling). Java has no direct equivalent, so the compat library
/// provides <c>StringHelper.concat()</c>.</para>
///
/// <para>This rewriter transforms:
/// <list type="bullet">
///   <item><c>String.Concat(a, b)</c> → <c>StringHelper.concat(a, b)</c></item>
///   <item><c>System.String.Concat(a, b)</c> → <c>StringHelper.concat(a, b)</c></item>
/// </list>
/// </para>
/// </summary>
public sealed class StringConcatRewriter : JavaSyntaxRewriter
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

        // Pattern: String.Concat(args) → StringHelper.concat(args)
        // Pattern: System.String.Concat(args) → StringHelper.concat(args)
        if (node.MethodName is "Concat" or "concat" && IsStringTarget(node.Target))
        {
            node.Target = new JavaIdentifierExpression("StringHelper");
            node.MethodName = "concat";
            _rewriteCount++;
        }

        return node;
    }

    private static bool IsStringTarget(JavaExpression? target)
    {
        // String.Concat(...)
        if (target is JavaIdentifierExpression id)
        {
            return id.Name is "String" or "System.String";
        }

        // System.String.Concat(...) — where System.String is a member access
        if (target is JavaMemberAccessExpression ma)
        {
            return ma.MemberName == "String"
                && ma.Target is JavaIdentifierExpression { Name: "System" };
        }

        return false;
    }
}
