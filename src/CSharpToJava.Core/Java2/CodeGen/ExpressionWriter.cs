// src/CSharpToJava.Core/Java2/CodeGen/ExpressionWriter.cs
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Java2.CodeGen;

public class ExpressionWriter
{
    private static readonly Dictionary<IrBinaryOp, (int Precedence, bool IsRightAssociative)> BinaryPrecedence = new()
    {
        { IrBinaryOp.Multiply, (12, false) }, { IrBinaryOp.Divide, (12, false) }, { IrBinaryOp.Modulo, (12, false) },
        { IrBinaryOp.Add, (10, false) }, { IrBinaryOp.Subtract, (10, false) },
        { IrBinaryOp.ShiftLeft, (9, false) }, { IrBinaryOp.ShiftRight, (9, false) }, { IrBinaryOp.UnsignedShiftRight, (9, false) },
        { IrBinaryOp.LessThan, (8, false) }, { IrBinaryOp.LessThanOrEqual, (8, false) },
        { IrBinaryOp.GreaterThan, (8, false) }, { IrBinaryOp.GreaterThanOrEqual, (8, false) },
        { IrBinaryOp.Equals, (7, false) }, { IrBinaryOp.NotEquals, (7, false) },
        { IrBinaryOp.BitwiseAnd, (6, false) },
        { IrBinaryOp.BitwiseXor, (5, false) },
        { IrBinaryOp.BitwiseOr, (4, false) },
        { IrBinaryOp.LogicalAnd, (3, false) },
        { IrBinaryOp.LogicalOr, (2, false) },
        { IrBinaryOp.NullCoalescing, (1, false) },
    };

    private static readonly Dictionary<IrUnaryOp, string> UnaryOpStrings = new()
    {
        { IrUnaryOp.Plus, "+" }, { IrUnaryOp.Minus, "-" }, { IrUnaryOp.Not, "!" },
        { IrUnaryOp.BitwiseNot, "~" },
        { IrUnaryOp.PreIncrement, "++" }, { IrUnaryOp.PreDecrement, "--" },
        { IrUnaryOp.PostIncrement, "++" }, { IrUnaryOp.PostDecrement, "--" },
    };

    private static readonly Dictionary<IrBinaryOp, string> BinaryOpStrings = new()
    {
        { IrBinaryOp.Add, "+" }, { IrBinaryOp.Subtract, "-" }, { IrBinaryOp.Multiply, "*" },
        { IrBinaryOp.Divide, "/" }, { IrBinaryOp.Modulo, "%" },
        { IrBinaryOp.LogicalAnd, "&&" }, { IrBinaryOp.LogicalOr, "||" },
        { IrBinaryOp.BitwiseAnd, "&" }, { IrBinaryOp.BitwiseOr, "|" }, { IrBinaryOp.BitwiseXor, "^" },
        { IrBinaryOp.ShiftLeft, "<<" }, { IrBinaryOp.ShiftRight, ">>" }, { IrBinaryOp.UnsignedShiftRight, ">>>" },
        { IrBinaryOp.Equals, "==" }, { IrBinaryOp.NotEquals, "!=" },
        { IrBinaryOp.LessThan, "<" }, { IrBinaryOp.LessThanOrEqual, "<=" },
        { IrBinaryOp.GreaterThan, ">" }, { IrBinaryOp.GreaterThanOrEqual, ">=" },
        { IrBinaryOp.NullCoalescing, "??" },
    };

    private static readonly Dictionary<IrAssignmentOp, string> AssignmentOpStrings = new()
    {
        { IrAssignmentOp.Assign, "=" }, { IrAssignmentOp.AddAssign, "+=" }, { IrAssignmentOp.SubtractAssign, "-=" },
        { IrAssignmentOp.MultiplyAssign, "*=" }, { IrAssignmentOp.DivideAssign, "/=" },
        { IrAssignmentOp.AndAssign, "&=" }, { IrAssignmentOp.OrAssign, "|=" }, { IrAssignmentOp.XorAssign, "^=" },
        { IrAssignmentOp.LeftShiftAssign, "<<=" }, { IrAssignmentOp.RightShiftAssign, ">>=" },
    };

    public string Write(IrExpression expr)
    {
        return expr switch
        {
            IrLiteralExpression lit => lit.Value,
            IrIdentifierExpression id => id.Name,
            IrThisExpression th => th.IsSuper ? "super" : "this",
            IrBinaryExpression bin => WriteBinary(bin),
            IrUnaryExpression un => WriteUnary(un),
            IrConditionalExpression cond => Write(cond.Condition) + " ? " + Write(cond.WhenTrue) + " : " + Write(cond.WhenFalse),
            IrCastExpression cast => "(" + cast.TargetType + ") " + Write(cast.Expression),
            IrNewExpression n => WriteNew(n),
            IrMemberAccessExpression mem => WriteMemberAccess(mem),
            IrInvocationExpression inv => WriteInvocation(inv),
            IrAssignmentExpression asgn => Write(asgn.Target) + " " + AssignmentOpStrings[asgn.Operator] + " " + Write(asgn.Value),
            IrArrayAccessExpression arr => Write(arr.Target) + "[" + Write(arr.Index) + "]",
            IrLambdaExpression lam => WriteLambda(lam),
            IrInstanceOfExpression inst => Write(inst.Expression) + " instanceof " + inst.TypeName + (inst.PatternVariable != null ? " " + inst.PatternVariable : ""),
            _ => "<unhandled expression: " + expr.GetType().Name + ">",
        };
    }

    private string WriteMemberAccess(IrMemberAccessExpression mem)
    {
        var target = Write(mem.Target);
        // Guid.Empty → new UUID(0L, 0L) — java.util.UUID has no Empty field.
        if (mem.MemberName == "Empty" && (target == "UUID" || target == "Guid"))
            return "new UUID(0L, 0L)";
        return target + "." + mem.MemberName;
    }

    private string WriteBinary(IrBinaryExpression bin)
    {
        var left = Write(bin.Left);
        var right = Write(bin.Right);
        var op = BinaryOpStrings[bin.Operator];
        var prec = BinaryPrecedence[bin.Operator].Precedence;

        if (bin.Left is IrBinaryExpression l && BinaryPrecedence[l.Operator].Precedence < prec)
            left = "(" + left + ")";
        if (bin.Right is IrBinaryExpression r)
        {
            var rPrec = BinaryPrecedence[r.Operator];
            if (rPrec.Precedence < prec || (rPrec.Precedence == prec && !BinaryPrecedence[bin.Operator].IsRightAssociative))
                right = "(" + right + ")";
        }

        // Object-typed comparison operators (<, <=, >, >=) are illegal in Java.
        // Emit "left.compareTo(right) OP 0" instead. Detect by looking at the operand's JavaType.
        if (bin.Operator is IrBinaryOp.LessThan or IrBinaryOp.LessThanOrEqual or
            IrBinaryOp.GreaterThan or IrBinaryOp.GreaterThanOrEqual)
        {
            var leftType = bin.Left.JavaType;
            var rightType = bin.Right.JavaType;
            if (!IsJavaPrimitive(leftType) || !IsJavaPrimitive(rightType))
            {
                return left + ".compareTo(" + right + ") " + op + " 0";
            }
        }

        return left + " " + op + " " + right;
    }

    private static bool IsJavaPrimitive(string? javaType)
    {
        if (string.IsNullOrEmpty(javaType)) return false;
        return javaType switch
        {
            "int" or "long" or "short" or "byte" or "double" or "float" or "char" or "boolean" => true,
            _ => false,
        };
    }

    private string WriteUnary(IrUnaryExpression un)
    {
        var op = UnaryOpStrings[un.Operator];
        var operand = Write(un.Operand);
        // Parenthesize binary expressions under unary operators
        if (un.Operand is IrBinaryExpression or IrConditionalExpression or IrAssignmentExpression)
            operand = "(" + operand + ")";
        return un.Operator is IrUnaryOp.PostIncrement or IrUnaryOp.PostDecrement
            ? operand + op : op + operand;
    }

    private string WriteNew(IrNewExpression n)
    {
        if (n.ArrayInitializer != null)
            return "new " + n.TypeName + " " + n.ArrayInitializer;
        return "new " + n.TypeName + "(" + string.Join(", ", n.Arguments.Select(Write)) + ")";
    }

    private string WriteInvocation(IrInvocationExpression inv)
    {
        var sb = new System.Text.StringBuilder();
        if (inv.Target != null)
            sb.Append(Write(inv.Target)).Append('.');
        sb.Append(inv.MethodName);
        if (inv.TypeArguments.Count > 0)
            sb.Append('<').Append(string.Join(", ", inv.TypeArguments)).Append('>');
        sb.Append('(').Append(string.Join(", ", inv.Arguments.Select(Write))).Append(')');
        return sb.ToString();
    }

    private string WriteLambda(IrLambdaExpression lam)
    {
        var sb = new System.Text.StringBuilder();
        if (lam.Parameters.Count == 1 && lam.Parameters[0].Type == null)
            sb.Append(lam.Parameters[0].Name);
        else
            sb.Append('(').Append(string.Join(", ", lam.Parameters.Select(p =>
                (p.Type != null ? p.Type + " " : "") + p.Name))).Append(')');
        sb.Append(" -> ");
        if (lam.ExpressionBody != null)
            sb.Append(Write(lam.ExpressionBody));
        else if (lam.BlockBody != null)
        {
            var stmtWriter = new StatementWriter();
            var innerWriter = new IndentedWriter();
            stmtWriter.Write(lam.BlockBody, innerWriter);
            sb.Append(innerWriter.ToString().Trim());
        }
        return sb.ToString();
    }
}
