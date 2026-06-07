using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 语句基类
/// </summary>
public abstract class JavaStatement : JavaSyntaxNode
{
    /// <summary>
    /// 语句前的注释（单行或多行）
    /// </summary>
    public string? LeadingComment { get; set; }

    protected void AppendLeadingComment(StringBuilder sb, string indentation)
    {
        JavaCommentEmitter.AppendLeadingComment(sb, indentation, LeadingComment);
    }

    /// <summary>
    /// 将语句体输出为块（如果不是 block 则自动包裹 {}）
    /// </summary>
    protected static void AppendBody(StringBuilder sb, string indentation, JavaStatement body)
    {
        if (body is JavaBlockStatement block)
        {
            sb.Append(block.ToString(indentation));
        }
        else
        {
            sb.AppendLine("{");
            sb.AppendLine(body.ToString(indentation + "    "));
            sb.Append(indentation).Append('}');
        }
    }
}

/// <summary>
/// 块语句 { ... }
/// </summary>
public class JavaBlockStatement : JavaStatement
{
    public List<JavaStatement> Statements { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.AppendLine("{");
        var inner = indentation + "    ";
        foreach (var stmt in Statements)
        {
            sb.AppendLine(stmt.ToString(inner));
        }
        sb.Append(indentation).Append('}');
        return sb.ToString();
    }
}

/// <summary>
/// 变量声明语句: Type name = initializer;
/// </summary>
public class JavaVariableDeclarationStatement : JavaStatement
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public JavaExpression? Initializer { get; set; }
    public bool IsFinal { get; set; }

    /// <summary>
    /// When set, indicates this variable is a holder (ref/out parameter wrapper).
    /// HolderType is the concrete holder type (e.g. "IntHolder", "ObjectHolder&lt;String&gt;"),
    /// DefaultInit is the default initialization expression (e.g. "new IntHolder()").
    /// </summary>
    public (string HolderType, string DefaultInit)? HolderInfo { get; set; }

    /// <summary>
    /// The resolved Java type of the initializer expression, when known.
    /// Used by IR validation rewriters to check type compatibility.
    /// </summary>
    public string? ResolvedInitializerType { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation);
        if (IsFinal) sb.Append("final ");
        sb.Append(Type).Append(' ').Append(Name);
        if (Initializer != null)
        {
            sb.Append(" = ").Append(Initializer.ToInlineString());
        }
        sb.Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// 表达式语句: expression;
/// </summary>
public class JavaExpressionStatement : JavaStatement
{
    public JavaExpression Expression { get; set; } = null!;

    public JavaExpressionStatement() { }
    public JavaExpressionStatement(JavaExpression expression)
    {
        Expression = expression;
    }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append(Expression.ToInlineString()).Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// return 语句: return expression;
/// </summary>
public class JavaReturnStatement : JavaStatement
{
    public JavaExpression? Expression { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("return");
        if (Expression != null)
        {
            sb.Append(' ').Append(Expression.ToInlineString());
        }
        sb.Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// if 语句: if (condition) { ... } else { ... }
/// </summary>
public class JavaIfStatement : JavaStatement
{
    public JavaExpression Condition { get; set; } = null!;
    public JavaStatement ThenBody { get; set; } = null!;
    public JavaStatement? ElseBody { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("if (").Append(Condition.ToInlineString()).Append(") ");

        AppendBody(sb, indentation, ThenBody);

        if (ElseBody != null)
        {
            sb.Append(" else ");
            if (ElseBody is JavaIfStatement)
            {
                // else if 链：不额外缩进
                sb.Append(ElseBody.ToString(indentation).TrimStart());
            }
            else
            {
                AppendBody(sb, indentation, ElseBody);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// for-each 语句: for (Type item : collection) { ... }
/// </summary>
public class JavaForEachStatement : JavaStatement
{
    public string VariableType { get; set; } = string.Empty;
    public string VariableName { get; set; } = string.Empty;
    public JavaExpression Collection { get; set; } = null!;
    public JavaStatement Body { get; set; } = null!;

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation)
          .Append("for (").Append(VariableType).Append(' ').Append(VariableName)
          .Append(" : ").Append(Collection.ToInlineString()).Append(") ");

        AppendBody(sb, indentation, Body);
        return sb.ToString();
    }
}

/// <summary>
/// for 语句: for (init; condition; increment) { ... }
/// </summary>
public class JavaForStatement : JavaStatement
{
    public string? Initializer { get; set; }
    public JavaExpression? Condition { get; set; }
    public string? Increment { get; set; }
    public JavaStatement Body { get; set; } = null!;

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("for (");
        sb.Append(Initializer ?? "");
        sb.Append("; ");
        if (Condition != null) sb.Append(Condition.ToInlineString());
        sb.Append("; ");
        sb.Append(Increment ?? "");
        sb.Append(") ");

        AppendBody(sb, indentation, Body);
        return sb.ToString();
    }
}

/// <summary>
/// while 语句: while (condition) { ... }
/// </summary>
public class JavaWhileStatement : JavaStatement
{
    public JavaExpression Condition { get; set; } = null!;
    public JavaStatement Body { get; set; } = null!;

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("while (").Append(Condition.ToInlineString()).Append(") ");
        AppendBody(sb, indentation, Body);
        return sb.ToString();
    }
}

/// <summary>
/// do-while 语句: do { ... } while (condition);
/// </summary>
public class JavaDoWhileStatement : JavaStatement
{
    public JavaExpression Condition { get; set; } = null!;
    public JavaStatement Body { get; set; } = null!;

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("do ");
        AppendBody(sb, indentation, Body);
        sb.Append(" while (").Append(Condition.ToInlineString()).Append(");");
        return sb.ToString();
    }
}

/// <summary>
/// try-catch-finally 语句
/// </summary>
public class JavaTryCatchStatement : JavaStatement
{
    /// <summary>
    /// try-with-resources 中的资源声明列表，例如 "InputStream stream = new FileInputStream(f)"
    /// </summary>
    public List<string> Resources { get; } = new();
    public JavaBlockStatement TryBody { get; set; } = new();
    public List<JavaCatchClause> CatchClauses { get; } = new();
    public JavaBlockStatement? FinallyBody { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("try");
        if (Resources.Count > 0)
        {
            sb.Append(" (");
            sb.Append(string.Join("; ", Resources));
            sb.Append(')');
        }
        sb.Append(' ').Append(TryBody.ToString(indentation));

        foreach (var catchClause in CatchClauses)
        {
            sb.Append(" catch (").Append(catchClause.ExceptionType);
            if (!string.IsNullOrEmpty(catchClause.VariableName))
            {
                sb.Append(' ').Append(catchClause.VariableName);
            }
            else
            {
                // Java requires a variable name in catch clauses (C# allows omitting it)
                sb.Append(" _ex");
            }
            sb.Append(") ").Append(catchClause.Body.ToString(indentation));
        }

        if (FinallyBody != null)
        {
            sb.Append(" finally ").Append(FinallyBody.ToString(indentation));
        }

        return sb.ToString();
    }
}

/// <summary>
/// catch 子句
/// </summary>
public class JavaCatchClause
{
    public string ExceptionType { get; set; } = "Exception";
    public string? VariableName { get; set; }
    public JavaBlockStatement Body { get; set; } = new();
}

/// <summary>
/// throw 语句: throw expression;
/// </summary>
public class JavaThrowStatement : JavaStatement
{
    public JavaExpression Expression { get; set; } = null!;

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("throw ").Append(Expression.ToInlineString()).Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// break 语句
/// </summary>
public class JavaBreakStatement : JavaStatement
{
    public string? Label { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("break");
        if (!string.IsNullOrEmpty(Label)) sb.Append(' ').Append(Label);
        sb.Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// continue 语句
/// </summary>
public class JavaContinueStatement : JavaStatement
{
    public string? Label { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("continue");
        if (!string.IsNullOrEmpty(Label)) sb.Append(' ').Append(Label);
        sb.Append(';');
        return sb.ToString();
    }
}

/// <summary>
/// switch 语句
/// </summary>
public class JavaSwitchStatement : JavaStatement
{
    public JavaExpression Expression { get; set; } = null!;
    public List<JavaSwitchSection> Sections { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);
        sb.Append(indentation).Append("switch (").Append(Expression.ToInlineString()).AppendLine(") {");

        var inner = indentation + "    ";
        foreach (var section in Sections)
        {
            foreach (var label in section.Labels)
            {
                sb.Append(inner).AppendLine(label);
            }
            var bodyIndent = inner + "    ";
            foreach (var stmt in section.Statements)
            {
                sb.AppendLine(stmt.ToString(bodyIndent));
            }
        }

        sb.Append(indentation).Append('}');
        return sb.ToString();
    }
}

/// <summary>
/// switch 分支
/// </summary>
public class JavaSwitchSection
{
    /// <summary>
    /// 标签列表，例如 "case 1:" 或 "default:"
    /// </summary>
    public List<string> Labels { get; } = new();
    public List<JavaStatement> Statements { get; } = new();
}

/// <summary>
/// 原始字符串语句 — 向后兼容回退，允许直接嵌入已生成的字符串代码
/// </summary>
public class JavaRawStatement : JavaStatement
{
    public string Code { get; set; } = string.Empty;

    public JavaRawStatement() { }
    public JavaRawStatement(string code)
    {
        Code = code;
    }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        AppendLeadingComment(sb, indentation);

        var lines = Code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(line))
            {
                sb.Append(indentation).Append(line.Trim());
            }
            if (i < lines.Length - 1)
            {
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}


