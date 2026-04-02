namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes .NET exception API usages that don't map to Java equivalents.
///
/// <para>Addresses several exception-related conversion issues:
/// <list type="bullet">
///   <item><c>ex.getInnerException()</c> → <c>ex.getCause()</c>
///         (.NET InnerException property → Java Throwable.getCause())</item>
///   <item><c>new ApplicationException(msg)</c> → <c>new RuntimeException(msg)</c>
///         (ApplicationException doesn't exist in Java)</item>
///   <item><c>new InvalidOperationException(msg)</c> → <c>new IllegalStateException(msg)</c></item>
///   <item><c>new ArgumentNullException(msg)</c> → <c>new NullPointerException(msg)</c></item>
///   <item><c>new ArgumentException(msg)</c> → <c>new IllegalArgumentException(msg)</c></item>
///   <item><c>new ArgumentOutOfRangeException(msg)</c> → <c>new IndexOutOfBoundsException(msg)</c></item>
///   <item><c>new NotImplementedException(msg)</c> → <c>new UnsupportedOperationException(msg)</c></item>
///   <item><c>new NotSupportedException(msg)</c> → <c>new UnsupportedOperationException(msg)</c></item>
///   <item>Catch clause types: same mappings applied to exception types in catch blocks</item>
/// </list>
/// </para>
/// </summary>
public sealed class ExceptionApiRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    /// <summary>
    /// Maps .NET exception type names (PascalCase) to Java equivalents.
    /// </summary>
    private static readonly Dictionary<string, string> ExceptionTypeMap = new(StringComparer.Ordinal)
    {
        ["ApplicationException"] = "RuntimeException",
        ["InvalidOperationException"] = "IllegalStateException",
        ["ArgumentNullException"] = "NullPointerException",
        ["ArgumentException"] = "IllegalArgumentException",
        ["ArgumentOutOfRangeException"] = "IndexOutOfBoundsException",
        ["NotImplementedException"] = "UnsupportedOperationException",
        ["NotSupportedException"] = "UnsupportedOperationException",
        ["ObjectDisposedException"] = "IllegalStateException",
        ["InvalidDataException"] = "IllegalArgumentException",
    };

    /// <summary>
    /// Maps .NET exception member names to Java equivalents.
    /// </summary>
    private static readonly Dictionary<string, string> ExceptionMethodMap = new(StringComparer.Ordinal)
    {
        ["getInnerException"] = "getCause",
        ["InnerException"] = "getCause",
    };

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

        // Pattern: ex.getInnerException() → ex.getCause()
        if (ExceptionMethodMap.TryGetValue(node.MethodName, out var replacement) &&
            node.Arguments.Count == 0 &&
            node.Target != null)
        {
            node.MethodName = replacement;
            _rewriteCount++;
        }

        return node;
    }

    public override JavaExpression VisitExpression(JavaExpression node)
    {
        var result = base.VisitExpression(node);

        // Pattern: ex.InnerException (member access) → ex.getCause()
        if (result is JavaMemberAccessExpression memberAccess &&
            ExceptionMethodMap.TryGetValue(memberAccess.MemberName, out var methodReplacement))
        {
            _rewriteCount++;
            return new JavaMethodCallExpression
            {
                Target = memberAccess.Target,
                MethodName = methodReplacement,
            };
        }

        return result;
    }

    public override JavaNewExpression VisitNewExpression(JavaNewExpression node)
    {
        node = (JavaNewExpression)base.VisitNewExpression(node);

        // Pattern: new ApplicationException(msg) → new RuntimeException(msg)
        if (ExceptionTypeMap.TryGetValue(node.Type, out var javaType))
        {
            node.Type = javaType;
            _rewriteCount++;
        }

        return node;
    }

    public override JavaTryCatchStatement VisitTryCatchStatement(JavaTryCatchStatement node)
    {
        node = (JavaTryCatchStatement)base.VisitTryCatchStatement(node);

        // Rewrite catch clause exception types
        foreach (var catchClause in node.CatchClauses)
        {
            if (ExceptionTypeMap.TryGetValue(catchClause.ExceptionType, out var javaCatchType))
            {
                catchClause.ExceptionType = javaCatchType;
                _rewriteCount++;
            }
        }

        return node;
    }

    public override JavaThrowStatement VisitThrowStatement(JavaThrowStatement node)
    {
        // The base visitor already handles the expression via VisitExpression/VisitNewExpression.
        return (JavaThrowStatement)base.VisitThrowStatement(node);
    }
}
