using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal sealed class ProjectUnsignedTypePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public UnsignedTypeRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>
    /// Explicit namespace mappings from the type-mapping config (e.g.
    /// System.Windows.Forms → dotnet.winforms.swing). Windows-only projects whose
    /// platform namespaces are covered are kept instead of excluded.
    /// </summary>
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectUnsignedTypePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required UnsignedTypeRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<UnsignedTypeRewriterDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectUnsignedTypePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-unsigned-src";

    public static async Task<ProjectUnsignedTypePreprocessResult> PreprocessAsync(
        ProjectUnsignedTypePreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Unsigned source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<UnsignedTypeRewriterDiagnostic>();
        var statistics = new UnsignedTypeRewriterStatistics();
        var rewriter = new UnsignedTypeRewriter();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        // Copy project files (.csproj, .sln), dropping excluded projects and
        // rewriting the solution file so the intermediate tree is self-consistent.
        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[unsigned-type]",
            request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            syntaxTrees[csFile] = CSharpSyntaxTree.ParseText(sourceCode, path: csFile);
        }

        var references = GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "unsigned-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var rewrittenSourceFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // The solution file was already copied (and possibly rewritten) in
                // the project-file loop; copying it again would restore the original.
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var result = rewriter.Rewrite(sourceCode, compilation.GetSemanticModel(tree), request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { Message = $"{relativePath}: {diagnostic.Message}" });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                rewrittenSourceFiles.Add(sourceFile);
                // 保留原文件的 BOM,让重写文件与原文件的 diff 只包含真实改动。
                await File.WriteAllTextAsync(
                    destinationFile,
                    result.OutputCode,
                    FileStartsWithBom(sourceFile)
                        ? new System.Text.UTF8Encoding(true)
                        : new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine($"[unsigned-type] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        if (statistics.OperatorsRewritten > 0 || statistics.CastsRewritten > 0)
        {
            await WriteExtensionFilesAsync(
                intermediateRoot, sourceRoot, layout, exclusionInfo, rewrittenSourceFiles, projectGraph);
        }

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }

            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[unsigned-type]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new UnsignedTypeRewriterDiagnostic(
                    UnsignedTypeSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }
        }

        return new ProjectUnsignedTypePreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == UnsignedTypeSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    internal static List<MetadataReference> GetBasicReferences()
    {
        var references = new List<MetadataReference>();
        var addedSimpleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))?.Split(Path.PathSeparator);
        if (trustedAssemblies != null)
        {
            foreach (var assembly in trustedAssemblies)
            {
                if (File.Exists(assembly))
                {
                    references.Add(MetadataReference.CreateFromFile(assembly));
                    addedSimpleNames.Add(Path.GetFileNameWithoutExtension(assembly));
                }
            }
        }
        else
        {
            var coreAssembly = typeof(object).Assembly;
            references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
            addedSimpleNames.Add(coreAssembly.GetName().Name ?? "System.Private.CoreLib");
            var runtimeDir = Path.GetDirectoryName(coreAssembly.Location)!;
            var runtimeAssembly = Path.Combine(runtimeDir, "System.Runtime.dll");
            if (File.Exists(runtimeAssembly))
            {
                references.Add(MetadataReference.CreateFromFile(runtimeAssembly));
            }
        }

        AddWindowsDesktopReferences(references, addedSimpleNames);
        return references;
    }

    private static void AddWindowsDesktopReferences(List<MetadataReference> references, HashSet<string> addedSimpleNames)
    {
        var coreRuntimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (string.IsNullOrEmpty(coreRuntimeDir))
        {
            return;
        }

        var windowsDesktopRoot = Path.GetFullPath(Path.Combine(coreRuntimeDir, "..", "..", "Microsoft.WindowsDesktop.App"));
        if (!Directory.Exists(windowsDesktopRoot))
        {
            return;
        }

        var currentVersion = Environment.Version.ToString(3);
        var versions = Directory.EnumerateDirectories(windowsDesktopRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrEmpty(v) && Version.TryParse(v, out _))
            .OrderByDescending(v => Version.Parse(v!))
            .ToList();
        var bestVersion = versions.FirstOrDefault(v => string.Equals(v, currentVersion, StringComparison.OrdinalIgnoreCase))
                          ?? versions.FirstOrDefault();
        if (bestVersion == null)
        {
            return;
        }

        foreach (var assembly in Directory.EnumerateFiles(Path.Combine(windowsDesktopRoot, bestVersion), "*.dll"))
        {
            var simpleName = Path.GetFileNameWithoutExtension(assembly);
            if (string.IsNullOrEmpty(simpleName) || !addedSimpleNames.Add(simpleName))
            {
                continue;
            }

            try
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
            catch (IOException)
            {
            }
            catch (BadImageFormatException)
            {
            }
        }
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static void MergeStatistics(UnsignedTypeRewriterStatistics target, UnsignedTypeRewriterStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.TypesReplaced += source.TypesReplaced;
        target.OperatorsRewritten += source.OperatorsRewritten;
        target.CastsRewritten += source.CastsRewritten;
        target.ArraysRewritten += source.ArraysRewritten;
        target.GenericArgsReplaced += source.GenericArgsReplaced;
        target.LiteralsRewritten += source.LiteralsRewritten;
    }

    /// <summary>
    /// 数学辅助文件只写一份到中间根目录；每个实际编译被重写源文件的项目通过
    /// Compile Include + Link 引用同一物理文件，不复制本地副本。扩展类为
    /// internal，每个程序集自包含一份。
    /// </summary>
    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph)
    {
        var content = UnsignedMathFileContent.Generate();
        var rootPath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);
        Directory.CreateDirectory(intermediateRoot);
        await File.WriteAllTextAsync(rootPath, content, new System.Text.UTF8Encoding(false));

        var hostProjectDirectories = ComputeAllExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles, projectGraph);
        EnsureExtensionCompileItems(sourceRoot, intermediateRoot, layout, exclusionInfo, hostProjectDirectories);
    }

    /// <summary>
    /// 计算需要承载扩展方法文件的项目目录：项目实际编译了至少一个被重写的源文件
    /// （目录内默认 glob，或 csproj 显式 Compile Include 解析到被重写文件）。
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

    /// <summary>
    /// 对每个编译被重写源文件的项目，把共享辅助文件追加到 csproj 的
    /// Compile Include（带 Link），并先清理旧的同名条目。
    /// </summary>
    private static void EnsureExtensionCompileItems(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories)
    {
        var sharedFilePath = Path.Combine(intermediateRoot, UnsignedMathFileContent.FileName);

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
            EnsureExtensionCompileItem(
                destinationProjectFile,
                UnsignedMathFileContent.FileName,
                Path.GetDirectoryName(destinationProjectFile)!,
                sharedFilePath);
        }
    }

    private static void EnsureExtensionCompileItem(
        string projectFilePath,
        string fileName,
        string projectDirectory,
        string sharedFilePath)
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

        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                Path.GetFullPath(projectDirectory).TrimEnd('\\', '/'),
                Path.GetFullPath(Path.GetDirectoryName(sharedFilePath)!).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase))
        {
            // 项目根目录就是辅助文件所在根目录且使用 SDK 默认 glob：
            // 文件已被默认编译项包含，追加显式条目会造成重复编译。
            return;
        }

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants().Where(e => e.Name.LocalName == "Compile").ToList())
        {
            var include = (string?)compileElement.Attribute("Include");
            var link = (string?)compileElement.Attribute("Link");
            if (MatchesHelperFile(include, normalizedFileName)
                || MatchesHelperFile(link, normalizedFileName))
            {
                compileElement.Remove();
            }
        }

        var relativeInclude = Path.GetRelativePath(projectDirectory, sharedFilePath);
        var compileItem = new XElement(
            "Compile",
            new XAttribute("Include", relativeInclude),
            new XAttribute("Link", fileName));
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup", compileItem),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool MatchesHelperFile(string? value, string normalizedFileName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value!.Replace('\\', '/');
        return normalized.Equals(normalizedFileName, StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("/" + normalizedFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

}
