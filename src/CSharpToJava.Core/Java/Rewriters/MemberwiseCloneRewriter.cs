namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that replaces calls to <c>memberwiseClone()</c> (from C#'s <c>MemberwiseClone()</c>)
/// with the Java-compatible <c>clone()</c>.
///
/// <para>Since <c>clone()</c> declares <c>CloneNotSupportedException</c> (a checked exception),
/// enclosing method bodies are wrapped with try-catch to re-throw as RuntimeException,
/// matching C# unchecked-exception semantics.</para>
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
        if (_needsCloneException)
        {
            WrapMethodBodyForClone(result);
        }
        return result;
    }

    public override JavaConstructorDeclaration VisitConstructorDeclaration(JavaConstructorDeclaration node)
    {
        _needsCloneException = false;
        var result = base.VisitConstructorDeclaration(node);
        if (_needsCloneException)
        {
            WrapConstructorBodyForClone(result);
        }
        return result;
    }

    public override JavaMethodCallExpression VisitMethodCallExpression(JavaMethodCallExpression node)
    {
        node = (JavaMethodCallExpression)base.VisitMethodCallExpression(node);

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

    private static void WrapMethodBodyForClone(JavaMethodDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            var tryStmt = new JavaTryCatchStatement();
            tryStmt.TryBody = new JavaBlockStatement();
            foreach (var stmt in node.StructuredBody.Statements)
                tryStmt.TryBody.Statements.Add(stmt);

            var catchBody = new JavaBlockStatement();
            catchBody.Statements.Add(new JavaRawStatement("throw new RuntimeException(e);"));
            tryStmt.CatchClauses.Add(new JavaCatchClause
            {
                ExceptionType = "Exception",
                VariableName = "e",
                Body = catchBody,
            });

            node.StructuredBody = new JavaMethodBody { Statements = { tryStmt } };
        }
        else if (!string.IsNullOrWhiteSpace(node.Body))
        {
            var content = node.IsBodyExpression
                ? $"return {node.Body.TrimEnd(';')};"
                : node.Body;
            node.Body = $"try {{\n        {content}\n    }} catch (Exception e) {{\n        throw new RuntimeException(e);\n    }}";
            node.IsBodyExpression = false;
        }
    }

    private static void WrapConstructorBodyForClone(JavaConstructorDeclaration node)
    {
        if (node.StructuredBody != null)
        {
            var tryStmt = new JavaTryCatchStatement();
            tryStmt.TryBody = new JavaBlockStatement();
            foreach (var stmt in node.StructuredBody.Statements)
                tryStmt.TryBody.Statements.Add(stmt);

            var catchBody = new JavaBlockStatement();
            catchBody.Statements.Add(new JavaRawStatement("throw new RuntimeException(e);"));
            tryStmt.CatchClauses.Add(new JavaCatchClause
            {
                ExceptionType = "Exception",
                VariableName = "e",
                Body = catchBody,
            });

            node.StructuredBody = new JavaMethodBody { Statements = { tryStmt } };
        }
        else if (!string.IsNullOrWhiteSpace(node.Body))
        {
            node.Body = $"try {{\n        {node.Body}\n    }} catch (Exception e) {{\n        throw new RuntimeException(e);\n    }}";
        }
    }
}
