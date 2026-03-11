using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 类型声明基类
/// </summary>
public abstract class JavaTypeDeclaration : JavaSyntaxNode
{
    public string Name { get; set; } = string.Empty;
    public List<JavaAnnotation> Annotations { get; } = new();
    public JavaModifiers Modifiers { get; set; } = JavaModifiers.None;
    public List<JavaTypeParameter> TypeParameters { get; } = new();

    protected void WriteModifiers(StringBuilder sb)
    {
        if (Modifiers != JavaModifiers.None)
        {
            sb.Append(GetModifiersString()).Append(' ');
        }
    }

    protected string GetModifiersString()
    {
        var modifiers = new List<string>();

        if ((Modifiers & JavaModifiers.Public) != 0) modifiers.Add("public");
        if ((Modifiers & JavaModifiers.Protected) != 0) modifiers.Add("protected");
        if ((Modifiers & JavaModifiers.Private) != 0) modifiers.Add("private");
        if ((Modifiers & JavaModifiers.Static) != 0) modifiers.Add("static");
        if ((Modifiers & JavaModifiers.Final) != 0) modifiers.Add("final");
        if ((Modifiers & JavaModifiers.Abstract) != 0) modifiers.Add("abstract");
        if ((Modifiers & JavaModifiers.Sealed) != 0) modifiers.Add("final"); // Java 的 sealed 不同
        if ((Modifiers & JavaModifiers.Override) != 0) modifiers.Add("@Override");

        return string.Join(" ", modifiers);
    }

    protected void WriteAnnotations(StringBuilder sb)
    {
        foreach (var annotation in Annotations)
        {
            sb.Append(annotation.ToString()).Append(' ');
        }
    }

    protected void WriteTypeParameters(StringBuilder sb)
    {
        if (TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
        }
    }
}

/// <summary>
/// Java 类声明
/// </summary>
public class JavaClassDeclaration : JavaTypeDeclaration
{
    public string? ExtendedType { get; set; }
    public List<string> ImplementedTypes { get; } = new();
    public List<JavaFieldDeclaration> Fields { get; } = new();
    public List<JavaMethodDeclaration> Methods { get; } = new();
    public List<JavaConstructorDeclaration> Constructors { get; } = new();
    public List<JavaTypeDeclaration> NestedTypes { get; } = new();
    public bool IsRecord { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        WriteAnnotations(sb);
        WriteModifiers(sb);

        if (IsRecord)
        {
            sb.Append("record ");
        }
        else if ((Modifiers & JavaModifiers.Abstract) != 0)
        {
            sb.Append("abstract class ");
        }
        else if ((Modifiers & JavaModifiers.Final) != 0)
        {
            sb.Append("final class ");
        }
        else
        {
            sb.Append("class ");
        }

        sb.Append(Name);
        WriteTypeParameters(sb);

        if (!string.IsNullOrEmpty(ExtendedType))
        {
            sb.Append(" extends ").Append(ExtendedType);
        }

        if (ImplementedTypes.Count > 0)
        {
            sb.Append(" implements ").Append(string.Join(", ", ImplementedTypes));
        }

        sb.AppendLine(" {");

        // 字段
        foreach (var field in Fields)
        {
            sb.Append(innerIndentation).AppendLine(field.ToString());
        }

        if (Fields.Count > 0 && (Constructors.Count > 0 || Methods.Count > 0))
        {
            sb.AppendLine();
        }

        // 构造函数
        foreach (var ctor in Constructors)
        {
            sb.Append(innerIndentation).AppendLine(ctor.ToString());
        }

        if (Constructors.Count > 0 && Methods.Count > 0)
        {
            sb.AppendLine();
        }

        // 方法
        foreach (var method in Methods)
        {
            sb.Append(innerIndentation).AppendLine(method.ToString());
        }

        // 嵌套类型
        foreach (var nested in NestedTypes)
        {
            sb.Append(nested.ToString(innerIndentation));
        }

        sb.AppendLine("}");

        return sb.ToString();
    }
}

/// <summary>
/// Java 接口声明
/// </summary>
public class JavaInterfaceDeclaration : JavaTypeDeclaration
{
    public List<string> ExtendedTypes { get; } = new();
    public List<JavaMethodDeclaration> Methods { get; } = new();
    public List<JavaFieldDeclaration> Fields { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        WriteAnnotations(sb);
        sb.Append("interface ");

        sb.Append(Name);
        WriteTypeParameters(sb);

        if (ExtendedTypes.Count > 0)
        {
            sb.Append(" extends ").Append(string.Join(", ", ExtendedTypes));
        }

        sb.AppendLine(" {");

        // 字段（默认 public static final）
        foreach (var field in Fields)
        {
            sb.Append(innerIndentation).AppendLine(field.ToString());
        }

        if (Fields.Count > 0 && Methods.Count > 0)
        {
            sb.AppendLine();
        }

        // 方法（默认 public abstract）
        foreach (var method in Methods)
        {
            sb.Append(innerIndentation).AppendLine(method.ToString());
        }

        sb.AppendLine("}");

        return sb.ToString();
    }
}

/// <summary>
/// Java 枚举声明
/// </summary>
public class JavaEnumDeclaration : JavaTypeDeclaration
{
    public List<string> Values { get; } = new();
    public List<JavaFieldDeclaration> Fields { get; } = new();
    public List<JavaMethodDeclaration> Methods { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        WriteAnnotations(sb);
        sb.Append("enum ").AppendLine(Name);
        sb.AppendLine(" {");

        // 枚举值
        for (int i = 0; i < Values.Count; i++)
        {
            sb.Append(innerIndentation).Append(Values[i]);
            if (i < Values.Count - 1 || Fields.Count > 0 || Methods.Count > 0)
            {
                sb.Append(",");
            }
            sb.AppendLine();
        }

        if (Fields.Count > 0 || Methods.Count > 0)
        {
            sb.AppendLine(";");
            sb.AppendLine();

            foreach (var field in Fields)
            {
                sb.Append(innerIndentation).AppendLine(field.ToString());
            }

            if (Fields.Count > 0 && Methods.Count > 0)
            {
                sb.AppendLine();
            }

            foreach (var method in Methods)
            {
                sb.Append(innerIndentation).AppendLine(method.ToString());
            }
        }

        sb.AppendLine("}");

        return sb.ToString();
    }
}

/// <summary>
/// Java 类型参数
/// </summary>
public class JavaTypeParameter
{
    public string Name { get; set; } = string.Empty;
    public List<string> Bounds { get; } = new();

    public JavaTypeParameter(string name)
    {
        Name = name;
    }

    public override string ToString()
    {
        if (Bounds.Count == 0)
            return Name;

        return $"{Name} extends {string.Join(" & ", Bounds)}";
    }
}

/// <summary>
/// Java 注解
/// </summary>
public class JavaAnnotation
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Values { get; } = new();

    public JavaAnnotation(string name)
    {
        Name = name;
    }

    public override string ToString()
    {
        if (Values.Count == 0)
            return $"@{Name}";

        var parameters = string.Join(", ", Values.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"@{Name}({parameters})";
    }
}
