namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that replaces calls to <c>memberwiseClone()</c> (from C#'s <c>MemberwiseClone()</c>)
/// with the Java-compatible <c>clone()</c> wrapped in a try-catch, or delegates to
/// a compatibility helper <c>ObjectHelper.memberwiseClone(this)</c>.
///
/// <para>Addresses error pattern 09: C# <c>MemberwiseClone()</c> is translated literally to
/// <c>memberwiseClone()</c> which does not exist in Java. The correct approach is to use
/// <c>clone()</c> (requires <c>Cloneable</c> interface) or a compat helper.</para>
///
/// <para>This rewriter replaces <c>expr.memberwiseClone()</c> with
/// <c>expr.clone()</c> and adds <c>CloneNotSupportedException</c> to the enclosing
/// method's throws clause if needed.</para>
/// </summary>
public sealed class MemberwiseCloneRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    private bool _needsCloneException;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        _needsCloneException = false;
        var result = base.VisitMethodDeclaration(node);
        if (_needsCloneException &&
            !result.ThrownExceptions.Contains("CloneNotSupportedException"))
        {
            result.ThrownExceptions.Add("CloneNotSupportedException");
        }
        return result;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: expr.memberwiseClone() → expr.clone()
        if (node.MethodName is "memberwiseClone" or "MemberwiseClone"
            && node.Arguments.Count == 0
            && node.Target != null)
        {
            node.MethodName = "clone";
            _needsCloneException = true;
            _rewriteCount++;
        }

        return node;
    }
}
