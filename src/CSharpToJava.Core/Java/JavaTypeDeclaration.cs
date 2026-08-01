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
    /// <summary>
    /// Optional comment (Javadoc or line comment) emitted verbatim before the type declaration.
    /// May be multi-line; each newline-separated segment is emitted on its own line.
    /// </summary>
    public string? LeadingComment { get; set; }

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
        else if ((Modifiers & JavaModifiers.Protected) != 0) modifiers.Add("protected");
        else if ((Modifiers & JavaModifiers.Private) != 0) modifiers.Add("private");
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
/// A single component (positional parameter) of a Java record declaration.
/// e.g. for <c>record Point(int x, int y)</c> there are two components.
/// </summary>
public class JavaRecordComponent
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    public JavaRecordComponent(string type, string name)
    {
        Type = type;
        Name = name;
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
    public List<JavaStaticInitializerBlock> StaticInitializers { get; } = new();
    public List<JavaTypeDeclaration> NestedTypes { get; } = new();
    public bool IsRecord { get; set; }
    /// <summary>
    /// True when this class was converted from a C# struct. Used by AssignmentTransformer
    /// to expand <c>this = expr</c> to field-by-field copy (Java can't assign to <c>this</c>).
    /// </summary>
    public bool IsConvertedFromStruct { get; set; }
    /// <summary>
    /// True when this class should be emitted as a Java value class (JEP 401).
    /// Set when converting a C# readonly struct with --use-value-class option.
    /// </summary>
    public bool IsValueClass { get; set; }
    /// <summary>
    /// Positional component list for Java records. Populated by RecordTransformer when IsRecord is true.
    /// </summary>
    public List<JavaRecordComponent> RecordComponents { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        JavaCommentEmitter.AppendLeadingComment(sb, indentation, LeadingComment);

        sb.Append(indentation);
        WriteAnnotations(sb);
        WriteModifiers(sb);

        if (IsValueClass)
        {
            sb.Append("value ");
        }
        if (IsRecord)
        {
            sb.Append("record ");
        }
        else
        {
            sb.Append("class ");
        }

        sb.Append(Name);
        WriteTypeParameters(sb);

        // Java record component list: record Point(int x, int y) { }
        if (IsRecord && RecordComponents.Count > 0)
        {
            sb.Append('(');
            sb.Append(string.Join(", ", RecordComponents.Select(c => $"{c.Type} {c.Name}")));
            sb.Append(')');
        }

        if (!string.IsNullOrEmpty(ExtendedType))
        {
            sb.Append(" extends ").Append(ExtendedType);
        }

        if (ImplementedTypes.Count > 0)
        {
            // 去重：按擦除类型名过滤，保留最具体的（有泛型参数的优先）
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var iface in ImplementedTypes)
            {
                var dot = iface.IndexOf('<');
                var erasedName = dot >= 0 ? iface.Substring(0, dot).TrimEnd() : iface;
                if (!seen.ContainsKey(erasedName) || dot >= 0)
                    seen[erasedName] = iface;
            }
            sb.Append(" implements ").Append(string.Join(", ", seen.Values));
        }

        sb.AppendLine(" {");

        // 字段
        foreach (var field in Fields)
        {
            sb.Append(innerIndentation).AppendLine(field.ToString(innerIndentation));
        }

        if (Fields.Count > 0 && (Constructors.Count > 0 || Methods.Count > 0))
        {
            sb.AppendLine();
        }

        // 静态初始化块
        foreach (var staticBlock in StaticInitializers)
        {
            sb.Append(innerIndentation).AppendLine(staticBlock.ToString(innerIndentation));
        }

        if (StaticInitializers.Count > 0 && (Constructors.Count > 0 || Methods.Count > 0))
        {
            sb.AppendLine();
        }

        // 构造函数
        foreach (var ctor in Constructors)
        {
            sb.Append(innerIndentation).AppendLine(ctor.ToString(innerIndentation));
        }

        if (Constructors.Count > 0 && Methods.Count > 0)
        {
            sb.AppendLine();
        }

        // 方法
        foreach (var method in Methods)
        {
            sb.Append(innerIndentation).AppendLine(method.ToString(innerIndentation));
        }

        // 嵌套类型
        foreach (var nested in NestedTypes)
        {
            sb.Append(nested.ToString(innerIndentation));
        }

        sb.Append(indentation).AppendLine("}");

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
    public List<JavaTypeDeclaration> NestedTypes { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        JavaCommentEmitter.AppendLeadingComment(sb, indentation, LeadingComment);

        sb.Append(indentation);
        WriteAnnotations(sb);
        WriteModifiers(sb);
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
            sb.Append(innerIndentation).AppendLine(field.ToString(innerIndentation));
        }

        if (Fields.Count > 0 && Methods.Count > 0)
        {
            sb.AppendLine();
        }

        // 方法（默认 public abstract）
        foreach (var method in Methods)
        {
            sb.Append(innerIndentation).AppendLine(method.ToString(innerIndentation));
        }

        // 嵌套类型
        foreach (var nested in NestedTypes)
        {
            sb.Append(nested.ToString(innerIndentation));
        }

        sb.Append(indentation).AppendLine("}");

        return sb.ToString();
    }
}
public class JavaEnumDeclaration : JavaTypeDeclaration
{
    public List<string> Values { get; } = new();
    public List<JavaFieldDeclaration> Fields { get; } = new();
    public List<JavaConstructorDeclaration> Constructors { get; } = new();
    public List<JavaMethodDeclaration> Methods { get; } = new();

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();
        var innerIndentation = indentation + "    ";

        JavaCommentEmitter.AppendLeadingComment(sb, indentation, LeadingComment);

        sb.Append(indentation);
        WriteAnnotations(sb);
        WriteModifiers(sb);
        sb.Append("enum ");
        sb.Append(Name);
        sb.AppendLine(" {");

        // 枚举值
        bool hasEnumBody = Fields.Count > 0 || Constructors.Count > 0 || Methods.Count > 0;
        for (int i = 0; i < Values.Count; i++)
        {
            sb.Append(innerIndentation).Append(Values[i]);
            bool isLast = i == Values.Count - 1;
            if (!isLast)
            {
                sb.Append(",");
            }
            else if (hasEnumBody)
            {
                sb.Append(";");
            }
            sb.AppendLine();
        }

        if (hasEnumBody)
        {
            sb.AppendLine();

            foreach (var field in Fields)
            {
                sb.Append(innerIndentation).AppendLine(field.ToString(innerIndentation));
            }

            if (Fields.Count > 0 && (Constructors.Count > 0 || Methods.Count > 0))
            {
                sb.AppendLine();
            }

            foreach (var ctor in Constructors)
            {
                sb.Append(innerIndentation).AppendLine(ctor.ToString(innerIndentation));
            }

            if (Constructors.Count > 0 && Methods.Count > 0)
            {
                sb.AppendLine();
            }

            foreach (var method in Methods)
            {
                sb.Append(innerIndentation).AppendLine(method.ToString(innerIndentation));
            }
        }

        sb.Append(indentation).AppendLine("}");

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
