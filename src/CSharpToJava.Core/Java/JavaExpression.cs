using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 表达式基类
/// </summary>
public abstract class JavaExpression : JavaSyntaxNode
{
    /// <summary>
    /// 生成不含前导缩进的内联表达式字符串
    /// </summary>
    public virtual string ToInlineString() => ToString("");

    public override string ToString(string indentation)
    {
        return indentation + ToInlineString();
    }
}

/// <summary>
/// 方法调用: target.methodName(args) 或 methodName(args)
/// </summary>
public class JavaMethodCallExpression : JavaExpression
{
    public JavaExpression? Target { get; set; }
    public string MethodName { get; set; } = string.Empty;
    public List<JavaExpression> Arguments { get; } = new();
    public List<string> TypeArguments { get; } = new();

    public override string ToInlineString()
    {
        var sb = new StringBuilder();

        if (Target != null)
        {
            sb.Append(Target.ToInlineString()).Append('.');
        }

        sb.Append(MethodName);

        if (TypeArguments.Count > 0)
        {
            sb.Append('<').Append(string.Join(", ", TypeArguments)).Append('>');
        }

        sb.Append('(');
        sb.Append(string.Join(", ", Arguments.Select(a => a.ToInlineString())));
        sb.Append(')');

        return sb.ToString();
    }
}

/// <summary>
/// 成员访问: target.memberName
/// </summary>
public class JavaMemberAccessExpression : JavaExpression
{
    public JavaExpression Target { get; set; } = null!;
    public string MemberName { get; set; } = string.Empty;

    public override string ToInlineString()
    {
        return $"{Target.ToInlineString()}.{MemberName}";
    }
}

/// <summary>
/// 标识符（变量名、类型名）
/// </summary>
public class JavaIdentifierExpression : JavaExpression
{
    public string Name { get; set; } = string.Empty;

    public JavaIdentifierExpression() { }
    public JavaIdentifierExpression(string name)
    {
        Name = name;
    }

    public override string ToInlineString() => Name;
}

/// <summary>
/// 字面量: 1, "hello", true, null, 3.14
/// </summary>
public class JavaLiteralExpression : JavaExpression
{
    public string Value { get; set; } = string.Empty;

    public JavaLiteralExpression() { }
    public JavaLiteralExpression(string value)
    {
        Value = value;
    }

    public override string ToInlineString() => Value;
}

/// <summary>
/// 二元表达式: left op right
/// </summary>
public class JavaBinaryExpression : JavaExpression
{
    public JavaExpression Left { get; set; } = null!;
    public string Operator { get; set; } = string.Empty;
    public JavaExpression Right { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"{Left.ToInlineString()} {Operator} {Right.ToInlineString()}";
    }
}

/// <summary>
/// 一元表达式: op expr (前缀) 或 expr op (后缀)
/// </summary>
public class JavaUnaryExpression : JavaExpression
{
    public string Operator { get; set; } = string.Empty;
    public JavaExpression Operand { get; set; } = null!;
    public bool IsPostfix { get; set; }

    public override string ToInlineString()
    {
        return IsPostfix
            ? $"{Operand.ToInlineString()}{Operator}"
            : $"{Operator}{Operand.ToInlineString()}";
    }
}

/// <summary>
/// 三元条件表达式: condition ? trueExpr : falseExpr
/// </summary>
public class JavaConditionalExpression : JavaExpression
{
    public JavaExpression Condition { get; set; } = null!;
    public JavaExpression WhenTrue { get; set; } = null!;
    public JavaExpression WhenFalse { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"{Condition.ToInlineString()} ? {WhenTrue.ToInlineString()} : {WhenFalse.ToInlineString()}";
    }
}

/// <summary>
/// 类型转换表达式: (Type)expression
/// </summary>
public class JavaCastExpression : JavaExpression
{
    public string Type { get; set; } = string.Empty;
    public JavaExpression Expression { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"({Type}) {Expression.ToInlineString()}";
    }
}

/// <summary>
/// new 表达式: new Type(args)
/// </summary>
public class JavaNewExpression : JavaExpression
{
    public string Type { get; set; } = string.Empty;
    public List<JavaExpression> Arguments { get; } = new();
    /// <summary>
    /// 数组初始化器，例如 new int[] {1, 2, 3}
    /// </summary>
    public string? ArrayInitializer { get; set; }

    public override string ToInlineString()
    {
        var sb = new StringBuilder();
        sb.Append("new ").Append(Type);

        if (ArrayInitializer != null)
        {
            sb.Append(' ').Append(ArrayInitializer);
        }
        else
        {
            sb.Append('(');
            sb.Append(string.Join(", ", Arguments.Select(a => a.ToInlineString())));
            sb.Append(')');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Lambda 表达式: (params) -> body
/// </summary>
public class JavaLambdaExpression : JavaExpression
{
    public List<string> Parameters { get; } = new();
    /// <summary>
    /// 单表达式 body（与 BlockBody 互斥）
    /// </summary>
    public JavaExpression? ExpressionBody { get; set; }
    /// <summary>
    /// 块 body（与 ExpressionBody 互斥）
    /// </summary>
    public JavaBlockStatement? BlockBody { get; set; }

    public override string ToInlineString()
    {
        var sb = new StringBuilder();

        if (Parameters.Count == 1)
        {
            sb.Append(Parameters[0]);
        }
        else
        {
            sb.Append('(').Append(string.Join(", ", Parameters)).Append(')');
        }

        sb.Append(" -> ");

        if (ExpressionBody != null)
        {
            sb.Append(ExpressionBody.ToInlineString());
        }
        else if (BlockBody != null)
        {
            sb.Append(BlockBody.ToString(""));
        }

        return sb.ToString();
    }
}

/// <summary>
/// 数组/索引访问: target[index]
/// </summary>
public class JavaArrayAccessExpression : JavaExpression
{
    public JavaExpression Target { get; set; } = null!;
    public JavaExpression Index { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"{Target.ToInlineString()}[{Index.ToInlineString()}]";
    }
}

/// <summary>
/// 赋值表达式: target = value
/// </summary>
public class JavaAssignmentExpression : JavaExpression
{
    public JavaExpression Target { get; set; } = null!;
    public string Operator { get; set; } = "=";
    public JavaExpression Value { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"{Target.ToInlineString()} {Operator} {Value.ToInlineString()}";
    }
}

/// <summary>
/// 括号表达式: (expression)
/// </summary>
public class JavaParenthesizedExpression : JavaExpression
{
    public JavaExpression InnerExpression { get; set; } = null!;

    public override string ToInlineString()
    {
        return $"({InnerExpression.ToInlineString()})";
    }
}

/// <summary>
/// instanceof 表达式: expression instanceof Type
/// </summary>
public class JavaInstanceOfExpression : JavaExpression
{
    public JavaExpression Expression { get; set; } = null!;
    public string Type { get; set; } = string.Empty;
    /// <summary>
    /// Java 16+ 模式变量名，例如 obj instanceof String s
    /// </summary>
    public string? PatternVariable { get; set; }

    public override string ToInlineString()
    {
        var result = $"{Expression.ToInlineString()} instanceof {Type}";
        if (!string.IsNullOrEmpty(PatternVariable))
        {
            result += $" {PatternVariable}";
        }
        return result;
    }
}

/// <summary>
/// this 或 super 表达式
/// </summary>
public class JavaThisExpression : JavaExpression
{
    public bool IsSuper { get; set; }

    public override string ToInlineString() => IsSuper ? "super" : "this";
}

/// <summary>
/// 原始字符串表达式 — 向后兼容回退，允许直接嵌入已生成的字符串代码
/// </summary>
public class JavaRawExpression : JavaExpression
{
    public string Code { get; set; } = string.Empty;

    public JavaRawExpression() { }
    public JavaRawExpression(string code)
    {
        Code = code;
    }

    public override string ToInlineString() => Code;
}
