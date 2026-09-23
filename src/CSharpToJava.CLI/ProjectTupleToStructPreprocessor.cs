using System.Xml.Linq;
using CSharpToJava.Core.TupleToStruct;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.CLI;

internal sealed class ProjectTupleToStructPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public TupleToStructOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>
    /// Explicit namespace mappings from the type-mapping config (e.g.
    /// System.Windows.Forms → dotnet.winforms.swing). Windows-only projects whose
    /// platform namespaces are covered are kept instead of excluded.
    /// </summary>
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectTupleToStructPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required TupleToStructStatistics Statistics { get; init; }
    public required IReadOnlyList<TupleToStructDiagnostic> Diagnostics { get; init; }

    /// <summary>Path of the generated struct file, or null when the tree had no tuples.</summary>
    public string? GeneratedStructFilePath { get; init; }
}

/// <summary>
/// Project-level tuple-to-struct preprocessing: rewrites every tuple in the source tree
/// into references to generated readonly structs and writes the shared struct file into
/// the intermediate tree. Runs as stage 2 of the pipeline (right after local-function
/// lifting), so later stages (default-parameter stripping, readonly-struct making, ...)
/// see tuple-free source.
/// </summary>
internal static class ProjectTupleToStructPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-tuple-src";

    public static async Task<ProjectTupleToStructPreprocessResult> PreprocessAsync(
        ProjectTupleToStructPreprocessRequest request)
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
                    $"Tuple source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<TupleToStructDiagnostic>();
        var statistics = new TupleToStructStatistics();
        var preprocessor = new TupleToStructPreprocessor(request.RewriterOptions);
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[tuple-to-struct]",
            request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot, Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();

        var compilation = CreateCompilation(
            sourceFiles, layout, intermediateRoot, out var syntaxTrees);

        var rewrittenFiles = new List<string>();
        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // Solution files were already copied (and possibly rewritten) above.
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
            var fileDiagnostics = new List<TupleToStructDiagnostic>();
            var result = preprocessor.RewriteFile(
                sourceCode, compilation.GetSemanticModel(tree), statistics, fileDiagnostics);

            foreach (var diagnostic in fileDiagnostics)
            {
                diagnostics.Add(diagnostic with { Message = $"{relativePath}: {diagnostic.Message}" });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                rewrittenFiles.Add(sourceFile);
                // 保留原文件 BOM，让重写文件与原文件的 diff 只包含真实改动。
                await File.WriteAllTextAsync(
                    destinationFile,
                    result.OutputCode,
                    FileStartsWithBom(sourceFile)
                        ? new System.Text.UTF8Encoding(true)
                        : new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine(
                    $"[tuple-to-struct] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // 生成文件的分配：每个编译单元只生成它真正需要自己定义的形状。元素类型非
        // public 的形状（含 internal 类型）在每个使用它的编译单元各生成一份——共享
        // 源码会被多个程序集分别编译，且这类形状本就不可能跨程序集流动；全 public
        // 的形状只由"被其他使用方引用"的那个项目定义，其余使用方经项目引用复用。
        var generatedStructPaths = DistributeStructFiles(
            preprocessor,
            layout,
            exclusionInfo,
            intermediateRoot,
            sourceRoot,
            request.RewriterOptions,
            statistics,
            diagnostics,
            request.Verbose);

        var preprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot);

        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }

            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[tuple-to-struct]");
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new TupleToStructDiagnostic(
                    TupleToStructSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }
        }

        return new ProjectTupleToStructPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = preprocessedSourcePath,
            Success = !diagnostics.Any(d => d.Severity == TupleToStructSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
            GeneratedStructFilePath = generatedStructPaths.FirstOrDefault(),
        };
    }

    private static CSharpCompilation CreateCompilation(
        IReadOnlyList<string> sourceFiles,
        ProjectPreprocessSourceLayout layout,
        string intermediateRoot,
        out Dictionary<string, SyntaxTree> syntaxTrees)
    {
        syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in sourceFiles)
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceCode = File.ReadAllText(file);
            syntaxTrees[file] = CSharpSyntaxTree.ParseText(
                sourceCode,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: file);
        }

        // ImplicitUsings=enable 的项目依赖 SDK 生成的 global using；补齐后 Type/Task
        // 这类名字才能解析，否则元组元素类型会退化成 object。
        var globalUsings = new System.Text.StringBuilder();
        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(layout.SourceRoot, projectFile));
            var projectUsings = ImplicitGlobalUsings.BuildForProject(intermediateProjectFile, intermediateRoot);
            if (projectUsings != null)
            {
                globalUsings.AppendLine(projectUsings);
            }
        }

        if (globalUsings.Length > 0)
        {
            const string syntheticPath = "__cs2j_global_usings.g.cs";
            syntaxTrees[syntheticPath] = CSharpSyntaxTree.ParseText(
                globalUsings.ToString(),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: syntheticPath);
        }

        return CSharpCompilation.Create(
            "tuple-preprocess",
            syntaxTrees.Values,
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    /// <summary>
    /// 生成的 struct 文件只写一份到中间根目录；每个编译了被改写源文件的项目通过
    /// Compile Include + Link 引用同一物理文件，不复制本地副本。
    /// </summary>
    private static void EnsureStructCompileItems(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        string fileName,
        string sharedFilePath,
        bool verbose)
    {
        var hostProjectDirectories = ComputeHostProjects(
            layout, exclusionInfo, rewrittenFiles, sourceRoot, intermediateRoot);
        if (hostProjectDirectories.Count == 0)
        {
            if (verbose)
            {
                Console.WriteLine(
                    "[tuple-to-struct] no project explicitly lists the rewritten files; " +
                    "the generated struct file relies on SDK default globbing.");
            }

            return;
        }

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
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureCompileItem(
                destinationProjectFile,
                fileName,
                Path.GetDirectoryName(destinationProjectFile)!,
                sharedFilePath);
        }
    }

    /// <summary>
    /// 收集需要引用生成文件的项目目录：凡实际编译（目录内默认 glob、显式 Compile
    /// Include、或 <c>$(SharedDir)</c> link）至少一个被改写源文件的项目，都必须把
    /// 生成文件纳入自身编译——共享源码会被多个程序集分别编译，各程序集各自需要
    /// 一份（元素类型非 public 时生成 internal struct，正是为这种情形准备的）。
    /// </summary>
    private static IReadOnlySet<string> ComputeHostProjects(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        string sourceRoot,
        string intermediateRoot)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0)
        {
            return result;
        }

        // 中间树内的被改写文件路径（生成文件挂在中间树上）。
        var rewrittenInIntermediate = new HashSet<string>(
            rewrittenFiles.Select(file => Path.GetFullPath(
                Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, file)))),
            StringComparer.OrdinalIgnoreCase);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
            {
                continue;
            }

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);

            if (compiled == null)
            {
                // 解析不了的项目按目录保守判定。
                var projectDirectory = Path.GetDirectoryName(projectFile)!;
                if (rewrittenFiles.Any(file => ProjectPreprocessSource.IsSameOrDescendantPath(file, projectDirectory)))
                {
                    result.Add(projectDirectory);
                }

                continue;
            }

            if (compiled.Overlaps(rewrittenInIntermediate))
            {
                result.Add(Path.GetDirectoryName(projectFile)!);
            }
        }

        return result;
    }

    /// <summary>
    /// 分配并写出生成文件：每个项目只包含自己必须定义的形状，避免某个编译单元引用到
    /// 它看不见的类型，也避免同名公共类型重复出现在多个程序集。
    /// </summary>
    private static IReadOnlyList<string> DistributeStructFiles(
        TupleToStructPreprocessor preprocessor,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        string intermediateRoot,
        string sourceRoot,
        TupleToStructOptions options,
        TupleToStructStatistics statistics,
        List<TupleToStructDiagnostic> diagnostics,
        bool verbose)
    {
        var generatedPaths = new List<string>();
        var shapesByFile = preprocessor.ShapesByFile;
        statistics.StructsGenerated = preprocessor.StructNames.Count;
        if (shapesByFile.Count == 0)
        {
            return generatedPaths;
        }

        var projectFiles = layout.ProjectFiles
            .Where(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();

        // 项目 → 该项目编译的源文件集合、需要哪些形状。
        var shapesByProject = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            var compiled = ProjectCompileSet.ComputeCompiledFiles(intermediateProjectFile, intermediateRoot);
            var needed = new HashSet<string>(StringComparer.Ordinal);
            shapesByProject[projectFile] = needed;
            if (compiled == null)
            {
                continue;
            }

            foreach (var (filePath, shapeKeys) in shapesByFile)
            {
                var intermediateFile = Path.GetFullPath(
                    Path.Combine(intermediateRoot, Path.GetRelativePath(sourceRoot, filePath)));
                if (!compiled.Contains(intermediateFile))
                {
                    continue;
                }

                foreach (var key in shapeKeys)
                {
                    needed.Add(key);
                }
            }
        }

        var referenceGraph = BuildProjectReferenceGraph(projectFiles, intermediateRoot, sourceRoot);

        // 形状 → 使用它的项目。
        var usersByShape = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (projectFile, needed) in shapesByProject)
        {
            foreach (var key in needed)
            {
                if (!usersByShape.TryGetValue(key, out var users))
                {
                    users = new List<string>();
                    usersByShape[key] = users;
                }

                users.Add(projectFile);
            }
        }

        // 公共形状的唯一所有者：被其他所有使用方（传递）引用的那个项目。
        var publicOwners = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, users) in usersByShape)
        {
            if (users.Count == 1 || !preprocessor.IsShapePubliclyAccessible(key))
            {
                continue;
            }

            publicOwners[key] = users.FirstOrDefault(candidate =>
                users.All(other => IsReachable(candidate, other, referenceGraph)));
        }

        // 项目 → 它必须定义的形状。
        var defineByProject = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (projectFile, needed) in shapesByProject)
        {
            foreach (var key in needed)
            {
                var mustDefine = !preprocessor.IsShapePubliclyAccessible(key)
                    || !publicOwners.TryGetValue(key, out var owner)
                    || owner == null
                    || string.Equals(owner, projectFile, StringComparison.OrdinalIgnoreCase);
                if (!mustDefine)
                {
                    continue;
                }

                if (!defineByProject.TryGetValue(projectFile, out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    defineByProject[projectFile] = set;
                }

                set.Add(key);
            }
        }

        // 互不引用的项目共享 public 形状时无法判定唯一归属：各自生成一份并提示。
        foreach (var (key, users) in usersByShape)
        {
            if (users.Count <= 1
                || !preprocessor.IsShapePubliclyAccessible(key)
                || publicOwners.TryGetValue(key, out var owner) && owner != null)
            {
                continue;
            }

            diagnostics.Add(new TupleToStructDiagnostic(
                TupleToStructSeverity.Warning,
                $"Tuple shape '{key}' is used by projects that do not reference each other " +
                $"({string.Join(", ", users.Select(Path.GetFileNameWithoutExtension))}); " +
                "a copy is generated into each of them."));
        }

        var multipleFiles = defineByProject.Count > 1;
        foreach (var (projectFile, shapeKeys) in defineByProject)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectFile);
            var fileName = multipleFiles
                ? $"_Cs2jTupleTypes.{projectName}.cs"
                : options.GeneratedFileName;

            var content = preprocessor.GenerateStructFile(preprocessor.CloseShapeKeys(shapeKeys));
            if (content == null)
            {
                continue;
            }

            var rootPath = Path.Combine(intermediateRoot, fileName);
            if (!File.Exists(rootPath))
            {
                File.WriteAllText(rootPath, content, new System.Text.UTF8Encoding(false));
                generatedPaths.Add(rootPath);
            }

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureCompileItem(
                intermediateProjectFile,
                fileName,
                Path.GetDirectoryName(intermediateProjectFile)!,
                rootPath);

            if (verbose)
            {
                Console.WriteLine(
                    $"[tuple-to-struct] {projectName}: {shapeKeys.Count} shape(s) -> {fileName}");
            }
        }

        if (defineByProject.Count == 0 && verbose)
        {
            Console.WriteLine(
                "[tuple-to-struct] no project could be resolved for the generated shapes; " +
                "the struct file relies on SDK default globbing.");
        }

        return generatedPaths;
    }

    private static bool IsReachable(
        string from,
        string to,
        Dictionary<string, HashSet<string>> graph)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!graph.TryGetValue(from, out var direct))
        {
            return false;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(direct);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (string.Equals(current, to, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            if (graph.TryGetValue(current, out var next))
            {
                foreach (var item in next)
                {
                    pending.Push(item);
                }
            }
        }

        return false;
    }

    private static Dictionary<string, HashSet<string>> BuildProjectReferenceGraph(
        IReadOnlyList<string> projectFiles,
        string intermediateRoot,
        string sourceRoot)
    {
        var byDirectory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(projectFile))!
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            byDirectory[directory] = projectFile;
        }

        var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var projectFile in projectFiles)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            graph[projectFile] = set;

            var intermediateProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            foreach (var reference in ProjectCompileSet.ReadProjectReferences(intermediateProjectFile))
            {
                var referenceDirectory = Path.GetDirectoryName(Path.GetFullPath(reference));
                if (referenceDirectory == null)
                {
                    continue;
                }

                if (byDirectory.TryGetValue(
                        referenceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        out var target))
                {
                    set.Add(target);
                }
            }
        }

        return graph;
    }

    private static void EnsureCompileItem(
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

        var normalizedFileName = fileName.Replace('\\', '/');
        foreach (var compileElement in root.Descendants()
                     .Where(e => e.Name.LocalName == "Compile").ToList())
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
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement(
                "ItemGroup",
                new XElement(
                    "Compile",
                    new XAttribute("Include", relativeInclude),
                    new XAttribute("Link", fileName))),
            new XText(Environment.NewLine));

        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static string? GetPropertyValue(XElement root, string propertyName)
    {
        return root.Descendants()
            .Where(element => element.Name.LocalName == propertyName)
            .Select(element => (string?)element.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
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
}
