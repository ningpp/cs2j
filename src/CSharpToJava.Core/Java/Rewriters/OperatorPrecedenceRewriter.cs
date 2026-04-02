namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes operator precedence issues where a logical-not (<c>!</c>) is applied
/// to a non-boolean operand that is part of a comparison expression.
///
/// <para>Addresses error pattern 23: <c>!collection.length > 0</c> is parsed as
/// <c>(!collection.length) > 0</c> which is invalid in Java because <c>!</c> cannot
/// be applied to <c>int</c>. The fix rewrites the IR so the negation applies to the
/// entire comparison: <c>!(collection.length > 0)</c>.</para>
/// </summary>
public sealed class OperatorPrecedenceRewriter : JavaSyntaxRewriter
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
        // Let the base visitor handle traversal first
        var result = base.VisitExpression(node);

        // Detect pattern: Binary(Unary(!, intExpr), comparisonOp, right)
        // i.e. !collection.length > 0
        // Rewrite to: Unary(!, Paren(Binary(intExpr, comparisonOp, right)))
        // i.e. !(collection.length > 0)
        if (result is JavaBinaryExpression binary &&
            IsComparisonOperator(binary.Operator) &&
            binary.Left is JavaUnaryExpression { Operator: "!", IsPostfix: false } unary &&
            IsLikelyNonBooleanExpression(unary.Operand))
        {
            _rewriteCount++;
            return new JavaUnaryExpression
            {
                Operator = "!",
                IsPostfix = false,
                Operand = new JavaParenthesizedExpression
                {
                    InnerExpression = new JavaBinaryExpression
                    {
                        Left = unary.Operand,
                        Operator = binary.Operator,
                        Right = binary.Right,
                    },
                },
            };
        }

        return result;
    }

    private static bool IsComparisonOperator(string op)
        => op is ">" or "<" or ">=" or "<=" or "==" or "!=";

    private static bool IsLikelyNonBooleanExpression(JavaExpression expr)
    {
        // Member access like .length, .size, .count
        // Include both camelCase (Java convention) and PascalCase (C# residue from incomplete conversion)
        if (expr is JavaMemberAccessExpression memberAccess)
        {
            var name = memberAccess.MemberName;
            return name is "length" or "size" or "count" or "Count" or "Length";
        }
        // Method call like .size(), .length(), .count()
        if (expr is JavaMethodCallExpression call && call.Arguments.Count == 0)
        {
            var name = call.MethodName;
            return name is "size" or "length" or "count" or "getCount" or "getLength";
        }
        return false;
    }
}
