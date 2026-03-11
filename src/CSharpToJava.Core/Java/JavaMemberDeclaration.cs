using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 字段声明
/// </summary>
public class JavaFieldDeclaration : JavaSyntaxNode
{
    public List<JavaAnnotation> Annotations { get; } = new();
    public JavaModifiers Modifiers { get; set; } = JavaModifiers.None;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Initializer { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();

        foreach (var annotation in Annotations)
        {
            sb.Append(annotation.ToString()).Append(' ');
        }

        // 修饰符
        if ((Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        if ((Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((Modifiers & JavaModifiers.Final) != 0) sb.Append("final ");
        if ((Modifiers & JavaModifiers.Volatile) != 0) sb.Append("volatile ");
        if ((Modifiers & JavaModifiers.Transient) != 0) sb.Append("transient ");

        sb.Append(Type).Append(' ').Append(Name);

        if (!string.IsNullOrEmpty(Initializer))
        {
            sb.Append(" = ").Append(Initializer);
        }

        sb.Append(';');

        return sb.ToString();
    }
}

/// <summary>
/// Java 方法声明
/// </summary>
public class JavaMethodDeclaration : JavaSyntaxNode
{
    public List<JavaAnnotation> Annotations { get; } = new();
    public JavaModifiers Modifiers { get; set; } = JavaModifiers.None;
    public List<JavaTypeParameter> TypeParameters { get; } = new();
    public string ReturnType { get; set; } = "void";
    public string Name { get; set; } = string.Empty;
    public List<JavaParameter> Parameters { get; } = new();
    public List<string> ThrownExceptions { get; } = new();
    public string? Body { get; set; }
    public bool IsBodyExpression { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();

        foreach (var annotation in Annotations)
        {
            sb.AppendLine($"{indentation}{annotation.ToString()}");
        }

        // 修饰符
        if ((Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        if ((Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");
        if ((Modifiers & JavaModifiers.Static) != 0) sb.Append("static ");
        if ((Modifiers & JavaModifiers.Final) != 0) sb.Append("final ");
        if ((Modifiers & JavaModifiers.Abstract) != 0) sb.Append("abstract ");
        if ((Modifiers & JavaModifiers.Synchronized) != 0) sb.Append("synchronized ");
        if ((Modifiers & JavaModifiers.Default) != 0) sb.Append("default ");

        // 泛型参数
        if (TypeParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", TypeParameters.Select(tp => tp.ToString())));
            sb.Append('>');
            sb.Append(' ');
        }

        sb.Append(ReturnType).Append(' ').Append(Name).Append('(');

        // 参数
        sb.Append(string.Join(", ", Parameters.Select(p => p.ToString())));

        sb.Append(')');

        // 异常
        if (ThrownExceptions.Count > 0)
        {
            sb.Append(" throws ").Append(string.Join(", ", ThrownExceptions));
        }

        // 方法体
        if (Modifiers.HasFlag(JavaModifiers.Abstract) || Body == null)
        {
            sb.Append(';');
        }
        else if (IsBodyExpression)
        {
            sb.Append(" { ").Append(Body).Append(" }");
        }
        else
        {
            sb.AppendLine(" {");
            if (!string.IsNullOrEmpty(Body))
            {
                var lines = Body.Split('\n');
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        sb.AppendLine($"{indentation}    {line.Trim()}");
                    }
                }
            }
            sb.Append(indentation).Append('}');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Java 构造函数声明
/// </summary>
public class JavaConstructorDeclaration : JavaSyntaxNode
{
    public List<JavaAnnotation> Annotations { get; } = new();
    public JavaModifiers Modifiers { get; set; } = JavaModifiers.None;
    public string ClassName { get; set; } = string.Empty;
    public List<JavaParameter> Parameters { get; } = new();
    public List<string> ThrownExceptions { get; } = new();
    public string? Body { get; set; }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();

        foreach (var annotation in Annotations)
        {
            sb.AppendLine($"{indentation}{annotation.ToString()}");
        }

        // 修饰符
        if ((Modifiers & JavaModifiers.Public) != 0) sb.Append("public ");
        if ((Modifiers & JavaModifiers.Protected) != 0) sb.Append("protected ");
        if ((Modifiers & JavaModifiers.Private) != 0) sb.Append("private ");

        sb.Append(ClassName).Append('(');

        // 参数
        sb.Append(string.Join(", ", Parameters.Select(p => p.ToString())));

        sb.Append(')');

        // 异常
        if (ThrownExceptions.Count > 0)
        {
            sb.Append(" throws ").Append(string.Join(", ", ThrownExceptions));
        }

        // 方法体
        if (Body == null)
        {
            sb.Append(';');
        }
        else
        {
            sb.AppendLine(" {");
            if (!string.IsNullOrEmpty(Body))
            {
                var lines = Body.Split('\n');
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        sb.AppendLine($"{indentation}    {line.Trim()}");
                    }
                }
            }
            sb.Append(indentation).Append('}');
        }

        return sb.ToString();
    }
}

/// <summary>
/// Java 参数
/// </summary>
public class JavaParameter
{
    public List<JavaAnnotation> Annotations { get; } = new();
    public JavaModifiers Modifiers { get; set; } = JavaModifiers.None;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsVarArgs { get; set; }

    public JavaParameter(string type, string name)
    {
        Type = type;
        Name = name;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();

        foreach (var annotation in Annotations)
        {
            sb.Append(annotation.ToString()).Append(' ');
        }

        if ((Modifiers & JavaModifiers.Final) != 0) sb.Append("final ");

        sb.Append(Type);

        if (IsVarArgs)
        {
            sb.Append("...");
        }

        sb.Append(' ').Append(Name);

        return sb.ToString();
    }
}

/// <summary>
/// Java 修饰符标志
/// </summary>
[Flags]
public enum JavaModifiers
{
    None = 0,
    Public = 1 << 0,
    Protected = 1 << 1,
    Private = 1 << 2,
    Static = 1 << 3,
    Final = 1 << 4,
    Abstract = 1 << 5,
    Sealed = 1 << 6,
    Override = 1 << 7,
    Synchronized = 1 << 8,
    Volatile = 1 << 9,
    Transient = 1 << 10,
    Default = 1 << 11,  // 接口默认方法
    Native = 1 << 12,
}
