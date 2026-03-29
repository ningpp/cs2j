using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 方法体容器 — 持有结构化语句列表
/// </summary>
public class JavaMethodBody : JavaSyntaxNode
{
    public List<JavaStatement> Statements { get; } = new();

    public JavaMethodBody() { }
    public JavaMethodBody(IEnumerable<JavaStatement> statements)
    {
        Statements.AddRange(statements);
    }

    /// <summary>
    /// 将方法体渲染为不含外层大括号的代码块
    /// （大括号由 JavaMethodDeclaration/JavaConstructorDeclaration 负责）
    /// </summary>
    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Statements.Count; i++)
        {
            sb.Append(Statements[i].ToString(indentation));
            if (i < Statements.Count - 1)
            {
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 将方法体渲染为原始字符串格式（与当前 JavaMethodDeclaration.Body 的字符串兼容）
    /// </summary>
    public string ToBodyString()
    {
        if (Statements.Count == 0) return string.Empty;
        return ToString("");
    }
}
