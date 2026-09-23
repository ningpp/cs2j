using System.Xml.Linq;

namespace CSharpToJava.CLI;

/// <summary>
/// 计算项目实际编译哪些源文件。需要它而不是"项目目录下所有 .cs"是因为仓库里存在
/// 共享源码：同一个文件通过 <c>&lt;Compile Include="$(SharedDir)X.cs" Link="…" /&gt;</c>
/// 被 link 进多个项目（例如 cs-text 的 <c>Common/System/HexConverter.cs</c>），
/// 生成文件必须挂到每一个真正编译它的项目，否则会出现"类型不存在/不可访问"。
/// </summary>
internal static class ProjectCompileSet
{
    /// <summary>
    /// 列出中间树中该项目编译的源文件（绝对路径，位于中间树内）。
    /// 解析不了的项目返回 null，调用方应保守处理。
    /// </summary>
    internal static HashSet<string>? ComputeCompiledFiles(
        string intermediateProjectPath,
        string intermediateRoot)
    {
        if (!File.Exists(intermediateProjectPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(intermediateProjectPath, LoadOptions.None);
        }
        catch (Exception)
        {
            return null;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectPath))!;
        var properties = MSBuildPropertyReader.Read(
            projectDirectory, intermediateRoot, root);

        var isSdkProject = root.Attribute("Sdk") != null
            || root.Elements().Any(e => e.Name.LocalName == "Sdk");
        var defaultCompileItems = !IsFalse(properties.GetValueOrDefault("EnableDefaultCompileItems"))
            && !IsFalse(properties.GetValueOrDefault("EnableDefaultItems"));

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var element in root.Descendants())
        {
            var name = element.Name.LocalName;
            if (name is not ("Compile" or "ProjectReference"))
            {
                continue;
            }

            if (name == "ProjectReference")
            {
                continue;
            }

            var isRemove = element.Attribute("Remove") != null;
            var include = (string?)element.Attribute("Include") ?? (string?)element.Attribute("Remove");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var expanded = MSBuildPropertyReader.Expand(include!, properties, projectDirectory);
            foreach (var resolved in ResolveInclude(expanded, projectDirectory))
            {
                if (isRemove)
                {
                    removed.Add(resolved);
                }
                else
                {
                    included.Add(resolved);
                }
            }
        }

        // SDK 默认 glob：项目目录下的所有 .cs（排除 bin/obj 与显式 Remove）。
        if (isSdkProject && defaultCompileItems)
        {
            foreach (var file in EnumerateSourceFiles(projectDirectory))
            {
                included.Add(file);
            }
        }

        foreach (var file in removed)
        {
            included.Remove(file);
        }

        return included;
    }

    private static bool IsFalse(string? value) =>
        string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>项目的 ProjectReference 解析结果（绝对路径）。</summary>
    internal static IReadOnlyList<string> ReadProjectReferences(string intermediateProjectPath)
    {
        if (!File.Exists(intermediateProjectPath))
        {
            return Array.Empty<string>();
        }

        XDocument document;
        try
        {
            document = XDocument.Load(intermediateProjectPath, LoadOptions.None);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var root = document.Root;
        if (root == null)
        {
            return Array.Empty<string>();
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectPath))!;
        var properties = MSBuildPropertyReader.Read(
            projectDirectory, projectDirectory, root);
        var result = new List<string>();
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var expanded = MSBuildPropertyReader.Expand(include!, properties, projectDirectory);
            try
            {
                result.Add(Path.GetFullPath(Path.Combine(projectDirectory, expanded)));
            }
            catch (Exception)
            {
                // 非法路径忽略。
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var name = Path.GetFileName(current);
            if (name is "bin" or "obj" or "node_modules")
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current, "*.cs");
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    pending.Push(sub);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // 无法枚举的目录直接跳过。
            }
        }
    }

    /// <summary>
    /// 展开一条 Include 值：支持 <c>*</c>/<c>?</c>/<c>**</c> 通配符；无通配符时按相对路径解析。
    /// </summary>
    private static IEnumerable<string> ResolveInclude(string value, string projectDirectory)
    {
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        if (!normalized.Contains('*') && !normalized.Contains('?'))
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(projectDirectory, normalized));
            }
            catch (Exception)
            {
                yield break;
            }

            yield return full;
            yield break;
        }

        // 通配符形式：取静态前缀目录后递归匹配。
        var wildcardIndex = normalized.IndexOfAny(new[] { '*', '?' });
        var staticPrefix = normalized[..wildcardIndex];
        var lastSeparator = staticPrefix.LastIndexOf(Path.DirectorySeparatorChar);
        var baseDirectory = lastSeparator < 0
            ? projectDirectory
            : Path.GetFullPath(Path.Combine(projectDirectory, staticPrefix[..lastSeparator]));
        var pattern = normalized[(lastSeparator + 1)..];

        if (!Directory.Exists(baseDirectory))
        {
            yield break;
        }

        var recursive = pattern.Contains("**");
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filePattern = pattern.Replace("**" + Path.DirectorySeparatorChar, string.Empty)
            .Replace("**", "*");

        IEnumerable<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(baseDirectory, filePattern, searchOption);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var match in matches)
        {
            yield return Path.GetFullPath(match);
        }
    }
}

/// <summary>
/// SDK 隐式 global using 支持：项目的 <c>ImplicitUsings</c> 为 enable 时，SDK 会生成
/// <c>GlobalUsings.g.cs</c>，预处理器的一次性编译必须等价补上，否则 <c>Type</c>、
/// <c>Task</c> 这类依赖隐式 using 的名字会解析失败（元组元素类型会退化成 object）。
/// </summary>
internal static class ImplicitGlobalUsings
{
    /// <summary>Microsoft.NET.Sdk 的默认隐式 using 集合。</summary>
    private static readonly string[] DefaultNamespaces =
    {
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    };

    internal static string? BuildForProject(string intermediateProjectPath, string intermediateRoot)
    {
        if (!File.Exists(intermediateProjectPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(intermediateProjectPath, LoadOptions.None);
        }
        catch (Exception)
        {
            return null;
        }

        var root = document.Root;
        if (root == null)
        {
            return null;
        }

        var properties = MSBuildPropertyReader.Read(
            Path.GetDirectoryName(Path.GetFullPath(intermediateProjectPath))!, intermediateRoot, root);
        if (!string.Equals(
                properties.GetValueOrDefault("ImplicitUsings")?.Trim(),
                "enable",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("// <auto-generated> Implicit global usings mirrored from ImplicitUsings=enable </auto-generated>");
        foreach (var ns in DefaultNamespaces)
        {
            builder.Append("global using global::").Append(ns).AppendLine(";");
        }

        return builder.ToString();
    }
}

/// <summary>
/// 极简 MSBuild 属性求值：读取项目及其祖先 Directory.Build.props/targets 中定义的
/// 属性并展开 <c>$(Name)</c> 引用。只覆盖中间树里出现的形态（含
/// <c>$([MSBuild]::NormalizeDirectory('…','…'))</c>），不追求完整 MSBuild 语义。
/// </summary>
internal static class MSBuildPropertyReader
{
    internal static Dictionary<string, string> Read(
        string projectDirectory,
        string intermediateRoot,
        XElement projectRoot)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 从中间树根到项目目录依次应用（子目录覆盖父目录）。
        var chain = new List<string>();
        var current = Path.GetFullPath(projectDirectory);
        var fullRoot = Path.GetFullPath(intermediateRoot);
        while (current != null && current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            chain.Add(current);
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent == null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        chain.Reverse();
        foreach (var directory in chain)
        {
            foreach (var fileName in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var path = Path.Combine(directory, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var document = XDocument.Load(path, LoadOptions.None);
                    ApplyProperties(document.Root, directory, properties);
                }
                catch (Exception)
                {
                    // 解析失败的 props 忽略。
                }
            }
        }

        ApplyProperties(projectRoot, projectDirectory, properties);
        return properties;
    }

    private static void ApplyProperties(
        XElement? root,
        string directory,
        Dictionary<string, string> properties)
    {
        if (root == null)
        {
            return;
        }

        foreach (var group in root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            foreach (var property in group.Elements())
            {
                var value = property.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                properties[property.Name.LocalName] = Expand(value, properties, directory);
            }
        }
    }

    internal static string Expand(
        string value,
        IReadOnlyDictionary<string, string> properties,
        string directory)
    {
        var result = value;
        for (var pass = 0; pass < 8; pass++)
        {
            var start = result.IndexOf("$(", StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = FindClosingParen(result, start + 1);
            if (end < 0)
            {
                break;
            }

            var name = result[(start + 2)..end];
            string replacement;
            if (name.StartsWith("[MSBuild]::NormalizeDirectory(", StringComparison.Ordinal))
            {
                replacement = EvaluateNormalizeDirectory(name, properties, directory);
            }
            else if (string.Equals(name, "MSBuildThisFileDirectory", StringComparison.OrdinalIgnoreCase))
            {
                replacement = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            }
            else if (properties.TryGetValue(name, out var propertyValue))
            {
                replacement = propertyValue;
            }
            else
            {
                // 未知属性（外部 SDK 属性等）：按空值处理，路径解析失败会被忽略。
                replacement = string.Empty;
            }

            result = string.Concat(result.AsSpan(0, start), replacement, result.AsSpan(end + 1));
        }

        return result;
    }

    /// <summary>找到与 <paramref name="openIndex"/> 处 '(' 配对的 ')'（跳过引号内容）。</summary>
    private static int FindClosingParen(string text, int openIndex)
    {
        var depth = 0;
        var inQuotes = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (inQuotes)
            {
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static string EvaluateNormalizeDirectory(
        string invocation,
        IReadOnlyDictionary<string, string> properties,
        string directory)
    {
        // 形如 [MSBuild]::NormalizeDirectory('a', 'b', ...)
        var open = invocation.IndexOf('(');
        var close = invocation.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return string.Empty;
        }

        var inner = invocation[(open + 1)..close];
        var parts = inner.Split(',')
            .Select(part => part.Trim().Trim('\''))
            .Where(part => part.Length > 0)
            .Select(part => Expand(part, properties, directory))
            .ToArray();
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var combined = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            combined = Path.Combine(combined, parts[i]);
        }

        return Path.GetFullPath(combined).TrimEnd(Path.DirectorySeparatorChar)
               + Path.DirectorySeparatorChar;
    }
}
