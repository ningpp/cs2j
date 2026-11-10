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
    /// 项目编译集合解析结果：<see cref="Files"/> 为实际编译的源文件绝对路径集合；
    /// <see cref="DefaultItemsInEffect"/> 表示 SDK 默认 glob（项目目录下全部 *.cs
    /// 再扣除显式 Remove）确实参与了解析——只有此时“不在集合内”才是可靠的排除信号。
    /// </summary>
    internal sealed class ProjectCompiledSet
    {
        public required HashSet<string> Files { get; init; }
        public required bool DefaultItemsInEffect { get; init; }

        /// <summary>
        /// 语法求值是否“欠定”：找不到单一 TFM / 条件语法无法识别 / Include 依赖
        /// 未解析的外部属性（如 <c>$(CoreLibSharedDir)</c>）。为 true 时调用方可以
        /// 升级到真正的 MSBuild 求值。
        /// </summary>
        public bool UnderDetermined { get; init; }

        public string? UnderDeterminedReason { get; init; }
    }

    /// <summary>
    /// 列出项目编译的源文件（绝对路径）。<paramref name="root"/> 界定
    /// Directory.Build.props/targets 的向上查找边界：中间树调用传中间树根，
    /// 源码树调用传源码根。<paramref name="includeDefaultItems"/> 为 false 时只解析
    /// 显式 <c>&lt;Compile Include&gt;</c> 项——目录覆盖判定已包含 SDK 默认 glob
    /// （项目目录下全部 .cs），无需重复枚举。
    /// 解析不了的项目返回 null，调用方应保守处理。
    /// </summary>
    internal static HashSet<string>? ComputeCompiledFiles(
        string projectPath,
        string root,
        bool includeDefaultItems = true)
        => ComputeCompiledSet(projectPath, root, includeDefaultItems)?.Files;

    /// <summary>
    /// 同 <see cref="ComputeCompiledFiles"/>，额外报告 SDK 默认 glob 是否生效，
    /// 供调用方判断“集合外文件”能否被可靠排除（如 <c>&lt;Compile Remove&gt;</c>
    /// 掉的平台分片 *.Unix.cs）。
    /// </summary>
    internal static ProjectCompiledSet? ComputeCompiledSet(
        string projectPath,
        string root,
        bool includeDefaultItems = true,
        bool allowMsBuildEvaluation = false)
    {
        if (!File.Exists(projectPath))
        {
            return null;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectPath, LoadOptions.None);
        }
        catch (Exception)
        {
            return null;
        }

        var projectRootElement = document.Root;
        if (projectRootElement == null || !projectRootElement.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath))!;
        var propertyRead = MSBuildPropertyReader.ReadDetailed(
            projectDirectory, root, projectRootElement);
        var properties = propertyRead.Properties;
        var underDeterminedReasons = new List<string>(propertyRead.UnderDeterminedReasons);

        void MarkUnderDetermined(string reason)
        {
            if (!underDeterminedReasons.Contains(reason))
            {
                underDeterminedReasons.Add(reason);
            }
        }

        var isSdkProject = projectRootElement.Attribute("Sdk") != null
            || projectRootElement.Elements().Any(e => e.Name.LocalName == "Sdk");
        var defaultCompileItems = !IsFalse(properties.GetValueOrDefault("EnableDefaultCompileItems"))
            && !IsFalse(properties.GetValueOrDefault("EnableDefaultItems"));

        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Evaluate in MSBuild document order: SDK default items are imported
        // before the project body, so seed them first; then each <Compile
        // Include>/<Compile Remove> mutates the live item set in document
        // order. Bucketing removes for a final sweep is wrong — a file removed
        // and then re-included later in the project (e.g.
        // "<Compile Remove="**\*.cs" />" followed by
        // "<Compile Include="CorrectnessTest.cs" />") must survive.
        var defaultItemsInEffect = includeDefaultItems && isSdkProject && defaultCompileItems;
        if (defaultItemsInEffect)
        {
            foreach (var file in EnumerateSourceFiles(projectDirectory))
            {
                included.Add(file);
            }
        }

        foreach (var element in projectRootElement.Descendants())
        {
            if (element.Name.LocalName != "Compile")
            {
                continue;
            }

            // Conditions on the item itself and on enclosing ItemGroup / Target /
            // When elements gate the item. Unrecognized syntax is conservative
            // TRUE (keep the item) but marks the evaluation as under-determined.
            var condition = MSBuildPropertyReader.EvaluateConditionChain(
                element, properties, projectDirectory, MarkUnderDetermined);
            if (condition == MSBuildConditionResult.False)
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
            if (expanded.Contains("$(", StringComparison.Ordinal))
            {
                MarkUnderDetermined($"Include 含未解析属性：{include}");
            }

            var resolvedAny = false;
            foreach (var resolved in ResolveInclude(expanded, projectDirectory))
            {
                resolvedAny = true;
                if (!isRemove
                    && !File.Exists(resolved)
                    && include!.Contains("$(", StringComparison.Ordinal))
                {
                    // External shared source (e.g. $(CoreLibSharedDir)X.cs) collapsed
                    // to a project-relative path that does not exist: the syntax
                    // evaluator cannot know where it really lives.
                    MarkUnderDetermined($"外部共享源未解析：{include}");
                }

                if (isRemove)
                {
                    included.Remove(resolved);
                }
                else
                {
                    included.Add(resolved);
                }
            }

            if (!isRemove && !resolvedAny && include!.Contains("$(", StringComparison.Ordinal))
            {
                MarkUnderDetermined($"外部共享源未解析：{include}");
            }
        }

        var compiledSet = new ProjectCompiledSet
        {
            Files = included,
            DefaultItemsInEffect = defaultItemsInEffect,
            UnderDetermined = underDeterminedReasons.Count > 0,
            UnderDeterminedReason = underDeterminedReasons.Count > 0
                ? string.Join("; ", underDeterminedReasons)
                : null,
        };

        if (allowMsBuildEvaluation && compiledSet.UnderDetermined)
        {
            var evaluated = MsBuildCompileEvaluator.TryEvaluate(projectPath);
            if (evaluated is { Files.Count: > 0 })
            {
                return new ProjectCompiledSet
                {
                    Files = new HashSet<string>(
                        evaluated.Files.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase),
                    DefaultItemsInEffect = true,
                    UnderDetermined = false,
                    UnderDeterminedReason = null,
                };
            }
        }

        return compiledSet;
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
        var normalized = value
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim();
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
/// SDK global using 支持：项目的 <c>ImplicitUsings</c> 为 enable 时，SDK 会生成
/// <c>GlobalUsings.g.cs</c>；项目也可以用 <c>&lt;Using Include="…" /&gt;</c>
/// （含 <c>Static</c>/<c>Alias</c>/<c>Remove</c>）显式声明 global using。
/// 预处理器的一次性编译必须等价补上，否则 <c>Type</c>、<c>Task</c>、
/// <c>StringBuilder</c> 这类依赖隐式/项目级 using 的名字会解析失败（残留的
/// ?? 无法判定左值类型、元组元素类型退化成 object 等）。
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

    private readonly record struct UsingEntry(string Name, bool IsStatic, string? Alias);

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

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(intermediateProjectPath))!;
        var properties = MSBuildPropertyReader.Read(projectDirectory, intermediateRoot, root);

        // <Using Remove="X" /> —— 可同时移除 SDK 默认 using 与显式 Using 项。
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "Using"))
        {
            if (MSBuildPropertyReader.EvaluateConditionChain(element, properties, projectDirectory)
                == MSBuildConditionResult.False)
            {
                continue;
            }

            var remove = (string?)element.Attribute("Remove");
            if (string.IsNullOrWhiteSpace(remove))
            {
                continue;
            }

            var expanded = MSBuildPropertyReader.Expand(remove!, properties, projectDirectory).Trim();
            if (expanded.Length > 0)
            {
                removed.Add(NormalizeName(expanded));
            }
        }

        var entries = new List<UsingEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddEntry(string name, bool isStatic, string? alias)
        {
            name = NormalizeName(name);
            if (name.Length == 0 || removed.Contains(name))
            {
                return;
            }

            alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
            var key = $"{(alias != null ? alias + "=" : "")}{(isStatic ? "static " : "")}{name}";
            if (seen.Add(key))
            {
                entries.Add(new UsingEntry(name, isStatic, alias));
            }
        }

        if (string.Equals(
                properties.GetValueOrDefault("ImplicitUsings")?.Trim(),
                "enable",
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ns in DefaultNamespaces)
            {
                AddEntry(ns, isStatic: false, alias: null);
            }
        }

        // 项目文件内显式声明的 <Using Include="…" Static="true" Alias="…" />。
        foreach (var element in root.Descendants().Where(e => e.Name.LocalName == "Using"))
        {
            if (MSBuildPropertyReader.EvaluateConditionChain(element, properties, projectDirectory)
                == MSBuildConditionResult.False)
            {
                continue;
            }

            var include = (string?)element.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var name = MSBuildPropertyReader.Expand(include!, properties, projectDirectory).Trim();
            var isStatic = string.Equals(
                (string?)element.Attribute("Static"), "true", StringComparison.OrdinalIgnoreCase);
            var alias = (string?)element.Attribute("Alias");
            AddEntry(name, isStatic, alias);
        }

        if (entries.Count == 0)
        {
            return null;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("// <auto-generated> Global usings mirrored from ImplicitUsings and <Using> items </auto-generated>");
        foreach (var entry in entries)
        {
            if (entry.Alias != null)
            {
                builder.Append("global using ")
                    .Append(entry.Alias)
                    .Append(" = ")
                    .Append(entry.IsStatic ? "static " : "")
                    .Append("global::").Append(entry.Name).AppendLine(";");
            }
            else if (entry.IsStatic)
            {
                builder.Append("global using static global::").Append(entry.Name).AppendLine(";");
            }
            else
            {
                builder.Append("global using global::").Append(entry.Name).AppendLine(";");
            }
        }

        return builder.ToString();
    }

    /// <summary>去掉名称两端空白和已有的 <c>global::</c> 前缀，便于去重/Remove 匹配。</summary>
    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        const string globalPrefix = "global::";
        if (trimmed.StartsWith(globalPrefix, StringComparison.Ordinal))
        {
            trimmed = trimmed[globalPrefix.Length..];
        }

        return trimmed;
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
        => ReadDetailed(projectDirectory, intermediateRoot, projectRoot).Properties;

    /// <summary>
    /// 同 <see cref="Read"/>，额外报告语法求值是否欠定（无法选出单一 TFM、
    /// Condition 无法识别、属性值依赖未导入的 SDK 属性等）。
    /// </summary>
    internal static MSBuildPropertyReadResult ReadDetailed(
        string projectDirectory,
        string intermediateRoot,
        XElement projectRoot)
    {
        var state = new ReadState();

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
                    ApplyProperties(document.Root, directory, state);
                }
                catch (Exception)
                {
                    // 解析失败的 props 忽略。
                }
            }
        }

        ApplyProperties(projectRoot, projectDirectory, state);
        SelectTargetFramework(state);

        return new MSBuildPropertyReadResult
        {
            Properties = state.Properties,
            UnderDeterminedReasons = state.Reasons,
        };
    }

    private sealed class ReadState
    {
        public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Reasons { get; } = new();

        public void Mark(string reason)
        {
            if (!Reasons.Contains(reason, StringComparer.Ordinal))
            {
                Reasons.Add(reason);
            }
        }
    }

    /// <summary>
    /// 选定单一 TFM：优先 <c>TargetFramework</c>，否则取 <c>TargetFrameworks</c>
    /// 按 <c>;</c> 分割后的第一个非空项；并派生 <c>TargetFrameworkIdentifier</c>/
    /// <c>TargetFrameworkVersion</c>，供 Condition 求值使用。
    /// </summary>
    private static void SelectTargetFramework(ReadState state)
    {
        var properties = state.Properties;
        var targetFramework = FirstNonEmptyValue(properties.GetValueOrDefault("TargetFramework"));
        if (targetFramework == null)
        {
            var targetFrameworks = properties.GetValueOrDefault("TargetFrameworks");
            if (!string.IsNullOrWhiteSpace(targetFrameworks))
            {
                targetFramework = targetFrameworks
                    .Split(';', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(value => value.Length > 0);
            }
        }

        if (targetFramework == null)
        {
            return;
        }

        properties["TargetFramework"] = targetFramework;
        if (TryDeriveTargetFramework(targetFramework, out var identifier, out var version))
        {
            properties["TargetFrameworkIdentifier"] = identifier;
            if (!string.IsNullOrEmpty(version))
            {
                properties["TargetFrameworkVersion"] = version;
            }
        }
        else
        {
            state.Mark($"无法从 TargetFramework '{targetFramework}' 派生 TargetFrameworkIdentifier");
        }
    }

    private static string? FirstNonEmptyValue(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>把 TFM 映射为 MSBuild 的 TargetFrameworkIdentifier/Version。</summary>
    internal static bool TryDeriveTargetFramework(
        string targetFramework,
        out string identifier,
        out string? version)
    {
        identifier = string.Empty;
        version = null;

        var value = targetFramework.Trim();
        var dash = value.IndexOf('-');
        if (dash >= 0)
        {
            value = value[..dash];
        }

        if (value.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            identifier = ".NETStandard";
            var rest = value["netstandard".Length..];
            version = rest.Length > 0 ? "v" + rest : null;
            return true;
        }

        if (value.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            identifier = ".NETCoreApp";
            var rest = value["netcoreapp".Length..];
            version = rest.Length > 0 ? "v" + rest : null;
            return true;
        }

        if (value.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            && value.Length > 3
            && char.IsDigit(value[3]))
        {
            var rest = value["net".Length..];
            if (rest.Contains('.'))
            {
                // net5.0 / net8.0 / net10.0 → .NETCoreApp
                identifier = ".NETCoreApp";
                version = "v" + rest;
                return true;
            }

            // net48 / net462 / net35 → .NETFramework
            identifier = ".NETFramework";
            version = rest.Length switch
            {
                2 => $"v{rest[0]}.{rest[1]}",
                >= 3 => $"v{rest[0]}.{rest[1]}.{rest[2..]}",
                _ => null,
            };
            return true;
        }

        return false;
    }

    private static void ApplyProperties(
        XElement? root,
        string directory,
        ReadState state)
    {
        if (root == null)
        {
            return;
        }

        foreach (var group in root.Descendants().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            if (EvaluateConditionChain(group, state.Properties, directory, state.Mark)
                == MSBuildConditionResult.False)
            {
                continue;
            }

            foreach (var property in group.Elements())
            {
                if (EvaluateConditionChain(property, state.Properties, directory, state.Mark)
                    == MSBuildConditionResult.False)
                {
                    continue;
                }

                var value = property.Value.Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                var expanded = ExpandInternal(value, state.Properties, directory, out var hadUnknown);
                state.Properties[property.Name.LocalName] = expanded;

                var propertyName = property.Name.LocalName;
                if (hadUnknown
                    && (string.Equals(propertyName, "TargetFramework", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(propertyName, "TargetFrameworks", StringComparison.OrdinalIgnoreCase)))
                {
                    state.Mark($"{propertyName} 依赖未解析的 MSBuild 属性：{value}");
                }
            }
        }
    }

    /// <summary>
    /// 对元素自身及其祖先（PropertyGroup/ItemGroup/Target/When/…）上的
    /// <c>Condition</c> 依次求值。无法识别的语法按 TRUE 保守处理并通过
    /// <paramref name="onUnrecognized"/> 上报。
    /// </summary>
    internal static MSBuildConditionResult EvaluateConditionChain(
        XElement element,
        IReadOnlyDictionary<string, string> properties,
        string directory,
        Action<string>? onUnrecognized = null)
    {
        foreach (var current in element.AncestorsAndSelf())
        {
            if (current.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            var condition = (string?)current.Attribute("Condition");
            if (string.IsNullOrWhiteSpace(condition))
            {
                continue;
            }

            var result = MSBuildConditionEvaluator.Evaluate(condition, properties, directory);
            if (result == MSBuildConditionResult.False)
            {
                return MSBuildConditionResult.False;
            }

            if (result == MSBuildConditionResult.Unrecognized)
            {
                onUnrecognized?.Invoke($"无法解析的 Condition（{current.Name.LocalName}）：{condition}");
            }
        }

        return MSBuildConditionResult.True;
    }

    internal static string Expand(
        string value,
        IReadOnlyDictionary<string, string> properties,
        string directory)
        => ExpandInternal(value, properties, directory, out _);

    private static string ExpandInternal(
        string value,
        IReadOnlyDictionary<string, string> properties,
        string directory,
        out bool hadUnknownProperty)
    {
        hadUnknownProperty = false;
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
                hadUnknownProperty = true;
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

/// <summary>属性读取结果：属性包 + 语法求值欠定的原因集合。</summary>
internal sealed class MSBuildPropertyReadResult
{
    public required Dictionary<string, string> Properties { get; init; }

    public required IReadOnlyList<string> UnderDeterminedReasons { get; init; }

    public bool UnderDetermined => UnderDeterminedReasons.Count > 0;
}

internal enum MSBuildConditionResult
{
    True,
    False,
    Unrecognized,
}

/// <summary>
/// 极简 MSBuild Condition 求值器。支持 <c>'$(A)' == 'b'</c>、<c>!=</c>、
/// <c>and</c>/<c>or</c>（大小写不敏感）、<c>!</c>、成对括号、
/// <c>Exists('path')</c> 以及裸 <c>true</c>/<c>false</c>。属性先展开
/// （未知属性展开为空，符合 MSBuild 语义）；无法识别的语法返回
/// <see cref="MSBuildConditionResult.Unrecognized"/>，调用方保守按 TRUE 处理。
/// </summary>
internal static class MSBuildConditionEvaluator
{
    internal static MSBuildConditionResult Evaluate(
        string condition,
        IReadOnlyDictionary<string, string> properties,
        string directory)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return MSBuildConditionResult.True;
        }

        string expanded;
        try
        {
            expanded = MSBuildPropertyReader.Expand(condition, properties, directory);
        }
        catch (Exception)
        {
            return MSBuildConditionResult.Unrecognized;
        }

        try
        {
            var parser = new Parser(expanded, directory);
            var value = parser.ParseOr();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                return MSBuildConditionResult.Unrecognized;
            }

            return value ? MSBuildConditionResult.True : MSBuildConditionResult.False;
        }
        catch (FormatException)
        {
            return MSBuildConditionResult.Unrecognized;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly string _directory;
        private int _index;

        public Parser(string text, string directory)
        {
            _text = text;
            _directory = directory;
        }

        public bool AtEnd => _index >= _text.Length;

        public void SkipWhitespace()
        {
            while (!AtEnd && char.IsWhiteSpace(_text[_index]))
            {
                _index++;
            }
        }

        public bool ParseOr()
        {
            var value = ParseAnd();
            while (TryConsumeKeyword("or"))
            {
                var right = ParseAnd();
                value = value || right;
            }

            return value;
        }

        private bool ParseAnd()
        {
            var value = ParseUnary();
            while (TryConsumeKeyword("and"))
            {
                var right = ParseUnary();
                value = value && right;
            }

            return value;
        }

        private bool ParseUnary()
        {
            SkipWhitespace();
            if (Peek() == '!' && Peek(1) != '=')
            {
                _index++;
                return !ParseUnary();
            }

            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            SkipWhitespace();
            if (Peek() == '(')
            {
                _index++;
                var value = ParseOr();
                SkipWhitespace();
                if (Peek() != ')')
                {
                    throw new FormatException();
                }

                _index++;
                return value;
            }

            if (TryConsumeKeyword("exists"))
            {
                SkipWhitespace();
                if (Peek() != '(')
                {
                    throw new FormatException();
                }

                _index++;
                var path = ParseOperand();
                SkipWhitespace();
                if (Peek() != ')')
                {
                    throw new FormatException();
                }

                _index++;
                return Exists(path);
            }

            // MSBuild 先展开属性再比较：`$(Unknown) == 'x'` 展开后左操作数
            // 为空字符串，仍是一条合法的比较（而不是语法错误）。
            var left = IsComparisonOperatorAhead() ? string.Empty : ParseOperand();
            SkipWhitespace();
            if (Peek() == '=' && Peek(1) == '=')
            {
                _index += 2;
                var right = ParseRightOperand();
                return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (Peek() == '!' && Peek(1) == '=')
            {
                _index += 2;
                var right = ParseRightOperand();
                return !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            return ParseBoolean(left);
        }

        private bool IsComparisonOperatorAhead()
            => (Peek() == '=' && Peek(1) == '=')
               || (Peek() == '!' && Peek(1) == '=');

        private string ParseRightOperand()
        {
            SkipWhitespace();
            return AtEnd ? string.Empty : ParseOperand();
        }

        private string ParseOperand()
        {
            SkipWhitespace();
            if (AtEnd)
            {
                throw new FormatException();
            }

            var quote = Peek();
            if (quote is '\'' or '"')
            {
                _index++;
                var start = _index;
                while (!AtEnd && Peek() != quote)
                {
                    _index++;
                }

                if (AtEnd)
                {
                    throw new FormatException();
                }

                var quoted = _text[start.._index];
                _index++;
                return quoted;
            }

            var begin = _index;
            while (!AtEnd)
            {
                var c = Peek();
                if (char.IsWhiteSpace(c) || c is '(' or ')' or '!' or '=' or '<' or '>')
                {
                    break;
                }

                _index++;
            }

            if (_index == begin)
            {
                throw new FormatException();
            }

            return _text[begin.._index];
        }

        private bool TryConsumeKeyword(string keyword)
        {
            SkipWhitespace();
            if (_index + keyword.Length > _text.Length)
            {
                return false;
            }

            if (!_text.AsSpan(_index, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var after = _index + keyword.Length;
            if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] == '_'))
            {
                return false;
            }

            _index = after;
            return true;
        }

        private bool Exists(string path)
        {
            path = path.Trim();
            if (path.Length == 0)
            {
                return false;
            }

            try
            {
                var fullPath = Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(_directory, path);
                return File.Exists(fullPath) || Directory.Exists(fullPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ParseBoolean(string token)
        {
            var value = token.Trim();
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new FormatException();
        }

        private char Peek(int offset = 0)
        {
            var index = _index + offset;
            return index < _text.Length ? _text[index] : '\0';
        }
    }
}
