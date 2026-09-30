using System.Xml.Linq;
using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.CLI.Preprocessing.Adapters;

/// <summary>
/// Shared Cs2jUnsignedMath support-library release/injection machinery for the
/// unsigned-type pass and numeric-boundary (both emit calls into
/// <c>Cs2jUnsigned.UnsignedMath</c>). Extracted verbatim from
/// <see cref="UnsignedPassAdapter"/>; behavior of that pass is guarded by its
/// existing tests.
/// </summary>
internal static class Cs2jUnsignedMathInjection
{
    /// <summary>
    /// Release the embedded Cs2jUnsignedMath project into the intermediate tree
    /// root and inject a ProjectReference into every host project that compiles
    /// at least one rewritten source file.
    /// </summary>
    /// <param name="addWarning">
    /// Receives the TFM-below-net10 warning text (one per host project that needs it).
    /// </param>
    public static async Task ReleaseAndInjectAsync(
        PassTreeContext tree,
        IReadOnlyList<string> rewrittenSourceFiles,
        Action<string> addWarning)
    {
        ThrowOnNameCollision(tree.Layout);

        await UnsignedMathSupportFiles.ReleaseAsync(
            Path.Combine(tree.IntermediateRoot, UnsignedMathSupportFiles.LibDirectoryName));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            tree.Layout, tree.Exclusion, rewrittenSourceFiles, tree.ProjectGraph);
        EnsureExtensionProjectReferences(
            tree.SourceRoot, tree.IntermediateRoot, tree.Layout, tree.Exclusion,
            hostProjectDirectories, addWarning);
    }

    /// <summary>扫描源树中本 CLI 早前释放的支撑库目录（任意深度）。</summary>
    public static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, UnsignedMathSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedProjectFile(
                Path.Combine(dir, Cs2jUnsignedMathProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    public static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 用户解决方案中已有保留名项目时立即终止，不静默误识别。内容与 CLI 嵌入资源
    /// 一致（行尾规整后比较）的保留名项目视为本 CLI 早前释放的支撑库本体，放行。
    /// </summary>
    private static void ThrowOnNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p) &&
            !IsOwnReleasedProjectFile(p));
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jUnsignedMathProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j unsigned math support library; rename the project and retry.");
        }
    }

    private static bool IsOwnReleasedProjectFile(string projectFilePath)
    {
        try
        {
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(UnsignedMathSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    /// <summary>
    /// host TFM 低于 net10.0 时构建前告警（NU1201 会推迟到用户 dotnet build
    /// 中间树时才暴露，这里提前给出两条出路）。
    /// </summary>
    private static void WarnWhenTargetFrameworkBelowNet10(
        XElement root,
        string projectFilePath,
        Action<string> addWarning)
    {
        var tfm = ProjectUnsignedTypePreprocessor.GetFirstTargetFramework(root);
        if (ProjectUnsignedTypePreprocessor.IsTargetFrameworkAtLeastNet10(tfm))
        {
            return;
        }

        addWarning(
            $"{projectFilePath}: target framework '{tfm ?? "(none)"}' is below net10.0; the injected " +
            $"reference to {Cs2jUnsignedMathProject.ProjectFileName} (net10.0) will fail to restore/build " +
            "with NU1201. Upgrade the host TargetFramework to net10.0+, or report the case so " +
            "Cs2jUnsignedMath can multi-target.");
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        Action<string> addWarning)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            UnsignedMathSupportFiles.LibDirectoryName,
            Cs2jUnsignedMathProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDirectory))
            {
                continue;
            }

            var destinationProjectFile = Path.Combine(
                intermediateRoot,
                Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(
                destinationProjectFile, libProjectPath, intermediateRoot, addWarning);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath,
        string libProjectPath,
        string intermediateRoot,
        Action<string> addWarning)
    {
        if (!File.Exists(projectFilePath))
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace);
        }
        catch (Exception)
        {
            return;
        }

        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var projectDirectory = Path.GetDirectoryName(projectFilePath)!;

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDirectory, intermediateRoot))
        {
            var removePattern = Path.Combine(UnsignedMathSupportFiles.LibDirectoryName, "**");
            if (!root.Descendants().Any(e =>
                    e.Name.LocalName == "Compile"
                    && string.Equals((string?)e.Attribute("Remove"), removePattern, StringComparison.OrdinalIgnoreCase)))
            {
                root.Add(
                    new XText(Environment.NewLine + "  "),
                    new XElement("ItemGroup",
                        new XElement("Compile", new XAttribute("Remove", removePattern))),
                    new XText(Environment.NewLine));
            }
        }

        foreach (var referenceElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference")
                     .ToList())
        {
            var include = (string?)referenceElement.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
            if (PathsEqual(resolvedPath, libProjectPath))
            {
                referenceElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));

        WarnWhenTargetFrameworkBelowNet10(root, projectFilePath, addWarning);

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 计算需要承载扩展方法文件的项目目录：项目实际编译了至少一个被改写的源文件
    /// （目录内默认 glob，或 csproj 显式 Compile Include 解析到被改写文件）。
    /// </summary>
    private static IReadOnlySet<string> ComputeAllExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        if (projectGraph == null || rewrittenFiles.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ComputeProjectsCompilingRewrittenFiles(layout, exclusionInfo, rewrittenFiles);
    }

    /// <summary>
    /// 收集实际会编译至少一个被重写源文件的项目目录：目录内直接包含被重写文件
    /// （SDK 默认 glob 场景），或 csproj 的 Compile Include 解析到被重写文件。
    /// </summary>
    private static IReadOnlySet<string> ComputeProjectsCompilingRewrittenFiles(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
        {
            return result;
        }

        var rewrittenFileSet = new HashSet<string>(rewrittenFiles, StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, projectDirectory)))
            {
                result.Add(projectDirectory);
            }
        }

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var projectDirectory = Path.GetDirectoryName(projectFile)!;
            if (result.Contains(projectDirectory))
            {
                continue;
            }

            XDocument projectDocument;
            try
            {
                projectDocument = XDocument.Load(projectFile, LoadOptions.None);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var compileElement in projectDocument.Descendants().Where(e => e.Name.LocalName == "Compile"))
            {
                var include = (string?)compileElement.Attribute("Include");
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                var resolvedPath = Path.GetFullPath(Path.Combine(projectDirectory, include!));
                if (rewrittenFileSet.Contains(resolvedPath))
                {
                    result.Add(projectDirectory);
                    break;
                }
            }
        }

        return result;
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
