namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes <c>Math.signum()</c> calls when applied to integer arguments.
///
/// <para>Addresses error pattern 22: C#'s <c>Math.Sign(int)</c> returns <c>int</c>, but
/// Java's <c>Math.signum(double)</c> returns <c>double</c>. When the result is assigned
/// to an <c>int</c> variable, the lossy conversion fails.</para>
///
/// <para>This rewriter transforms:
/// <list type="bullet">
///   <item><c>Math.signum(intExpr)</c> → <c>Integer.signum(intExpr)</c></item>
///   <item><c>(int) Math.signum(expr)</c> → <c>Integer.signum(expr)</c> (removes redundant cast)</item>
/// </list>
/// Java's <c>Integer.signum(int)</c> accepts <c>int</c> and returns <c>int</c>,
/// matching C# semantics exactly.</para>
/// </summary>
public sealed class MathMethodRewriter : JavaSyntaxRewriter
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

        // Pattern: (int) Math.signum(x) → Integer.signum(x)
        if (result is JavaCastExpression { Type: "int" } cast &&
            cast.Expression is JavaMethodCallExpression innerCall &&
            IsMathSignum(innerCall))
        {
            RewriteToIntegerSignum(innerCall);
            _rewriteCount++;
            return innerCall; // Drop the cast
        }

        return result;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: Math.signum(intExpr) → Integer.signum(intExpr)
        // We detect "intExpr" heuristically (literal int, variable, etc.)
        if (IsMathSignum(node) && node.Arguments.Count == 1)
        {
            var arg = node.Arguments[0];
            if (IsLikelyIntExpression(arg))
            {
                RewriteToIntegerSignum(node);
                _rewriteCount++;
            }
        }

        // Pattern: Math.abs(intExpr) with int argument but might need Integer.parseInt
        // Math.abs() works fine in Java for both int and double, no rewrite needed.

        return node;
    }

    private static bool IsMathSignum(JavaMethodCallExpression call)
    {
        if (call.MethodName != "signum") return false;
        if (call.Target is JavaIdentifierExpression { Name: "Math" }) return true;
        if (call.Target is JavaMemberAccessExpression ma &&
            ma.MemberName == "Math") return true;
        return false;
    }

    private static void RewriteToIntegerSignum(JavaMethodCallExpression call)
    {
        call.Target = new JavaIdentifierExpression("Integer");
        call.MethodName = "signum";
    }

    private static bool IsLikelyIntExpression(JavaExpression expr)
    {
        // Integer literals (no dot, no f/d suffix)
        if (expr is JavaLiteralExpression lit)
        {
            var val = lit.Value;
            return val.Length > 0 && char.IsDigit(val[0]) && !val.Contains('.') && !val.Contains('f') && !val.Contains('d');
        }
        // Cast to int
        if (expr is JavaCastExpression { Type: "int" }) return true;
        // Variable or member access (can't determine type statically, skip)
        return false;
    }
}
