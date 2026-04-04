using System.Text;

namespace CSharpToJava.Core.Java;

/// <summary>
/// Java 编译单元（文件）
/// </summary>
public class JavaCompilationUnit : JavaSyntaxNode
{
    public string? Package { get; set; }
    public List<JavaImport> Imports { get; } = new();
    public List<JavaTypeDeclaration> TypeDeclarations { get; } = new();

    public JavaCompilationUnit(string? package = null)
    {
        Package = package;
    }

    public override string ToString(string indentation)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(Package))
        {
            sb.AppendLine($"package {Package};");
            sb.AppendLine();
        }

        // Collect wildcard package names to avoid emitting redundant explicit imports
        var wildcardPackages = new HashSet<string>(
            Imports.Where(i => i.IsWildcard && !i.IsStatic).Select(i => i.Name));

        foreach (var import in Imports)
        {
            // Skip explicit (non-wildcard) imports whose package is already covered by a wildcard
            if (!import.IsWildcard && !import.IsStatic)
            {
                var lastDot = import.Name.LastIndexOf('.');
                if (lastDot > 0 && wildcardPackages.Contains(import.Name[..lastDot]))
                    continue;
            }
            sb.AppendLine(import.ToCodeString(indentation));
        }

        if (Imports.Count > 0)
        {
            sb.AppendLine();
        }

        foreach (var type in TypeDeclarations)
        {
            sb.AppendLine(type.ToString(indentation));
        }

        return sb.ToString();
    }
}

/// <summary>
/// Java 导入声明
/// </summary>
public class JavaImport
{
    public string Name { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsWildcard { get; set; }

    public JavaImport(string name, bool isStatic = false, bool isWildcard = false)
    {
        Name = name;
        IsStatic = isStatic;
        IsWildcard = isWildcard;
    }

    public string ToCodeString(string indentation = "")
    {
        var staticPart = IsStatic ? "static " : "";
        var wildcardPart = IsWildcard ? ".*" : "";
        return $"{indentation}import {staticPart}{Name}{wildcardPart};";
    }

    public override string ToString()
    {
        return ToCodeString();
    }
}
