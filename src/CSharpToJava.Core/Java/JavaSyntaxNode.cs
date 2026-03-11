namespace CSharpToJava.Core.Java;

/// <summary>
/// Java AST 节点基类
/// </summary>
public abstract class JavaSyntaxNode
{
    public abstract string ToString(string indentation);
}

/// <summary>
/// 包装多个 Java 成员节点的集合
/// </summary>
public class JavaMemberCollection : JavaSyntaxNode
{
    public List<JavaSyntaxNode> Members { get; }

    public JavaMemberCollection(List<JavaSyntaxNode> members)
    {
        Members = members;
    }

    public JavaMemberCollection(params JavaSyntaxNode[] members)
    {
        Members = new List<JavaSyntaxNode>(members);
    }

    public override string ToString(string indentation)
    {
        return string.Join("\n\n", Members.Select(m => m.ToString(indentation)));
    }
}
