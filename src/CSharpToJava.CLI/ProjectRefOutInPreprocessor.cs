using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Xml.Linq;

namespace CSharpToJava.CLI;

internal sealed class ProjectRefOutInPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public RefOutInRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
}

internal sealed class ProjectRefOutInPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required RefOutInRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<RefOutInRewriterDiagnostic> Diagnostics { get; init; }
    public required IReadOnlyCollection<string> RequiredRefStructTypes { get; init; }
    public string? BuildLogSummary { get; init; }
    public string? TestLogSummary { get; init; }
}

internal static class ProjectRefOutInPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-refoutin-src";

    public static async Task<ProjectRefOutInPreprocessResult> PreprocessAsync(
        ProjectRefOutInPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ProjectPreprocessSource.ResolveLayout(request.SourcePath);
        var intermediateRoot = Path.Combine(
            Path.GetFullPath(request.DestinationRoot), IntermediateDirectoryName);

        if (Directory.Exists(intermediateRoot))
        {
            if (!request.Force)
            {
                throw new InvalidOperationException(
                    $"Ref/out/in source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<RefOutInRewriterDiagnostic>();
        var statistics = new RefOutInRewriterStatistics();
        var rewriter = new RefOutInRewriter();
        var sourceRoot = layout.SourceRoot;

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose,
                request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[ref-out-in]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var parseOptions = new CSharpParseOptions(
            preprocessorSymbols: new[] { "NETCOREAPP" });
        var syntaxTrees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var csFile in csFiles)
        {
            var sourceCode = await File.ReadAllTextAsync(csFile);
            var tree = CSharpSyntaxTree.ParseText(sourceCode, parseOptions, path: csFile);
            syntaxTrees[csFile] = tree;
        }

        var references = ProjectUnsignedTypePreprocessor.GetBasicReferences();
        var compilation = CSharpCompilation.Create(
            "refoutin-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        var allRefStructTypes = new HashSet<string>(StringComparer.Ordinal);
        var rewrittenSourceFiles = new List<string>();
        var ownReleaseDirectories = FindOwnReleaseLibDirectories(sourceRoot);

        foreach (var sourceFile in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            if (IsUnderOwnReleaseLib(sourceFile, ownReleaseDirectories))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                if (request.Verbose)
                    Console.WriteLine($"[ref-out-in] clean (own release copy): {relativePath}");
                continue;
            }

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var semanticModel = compilation.GetSemanticModel(tree);
            var result = rewriter.Rewrite(sourceCode, semanticModel, request.RewriterOptions);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Message = $"{relativePath}: {diagnostic.Message}"
                });
            }

            foreach (var rst in result.RequiredRefStructTypes)
                allRefStructTypes.Add(rst);

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                rewrittenSourceFiles.Add(sourceFile);
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
                    $"[ref-out-in] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Release support files and inject ProjectReferences
        if (statistics.RefParamsRewritten > 0 || statistics.OutParamsRewritten > 0
            || statistics.RefReturnsRewritten > 0 || statistics.RefLocalsRewritten > 0
            || statistics.RefFieldsRewritten > 0 || statistics.DelegatesRewritten > 0
            || allRefStructTypes.Count > 0)
        {
            await WriteExtensionFilesAsync(
                intermediateRoot, sourceRoot, layout, exclusionInfo,
                rewrittenSourceFiles, projectGraph, allRefStructTypes, diagnostics);
        }

        // Restore + verify build/tests
        string? buildSummary = null;
        string? testSummary = null;
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[ref-out-in]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new RefOutInRewriterDiagnostic(
                    "ROI100",
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}.",
                    RefOutInSeverity.Error));
            }

            if (request.VerifyBuild)
            {
                buildSummary = await RunDotNetAsync("build", restoreTarget, request.Verbose, "Release");
                if (buildSummary.StartsWith("EXIT:"))
                {
                    diagnostics.Add(new RefOutInRewriterDiagnostic(
                        "ROI101",
                        $"dotnet build failed after ref/out/in preprocessing: {buildSummary}",
                        RefOutInSeverity.Error));
                }
            }

            if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
            {
                testSummary = await RunDotNetAsync("test", restoreTarget, request.Verbose, "Release");
                if (testSummary.StartsWith("EXIT:"))
                {
                    diagnostics.Add(new RefOutInRewriterDiagnostic(
                        "ROI102",
                        $"dotnet test failed after ref/out/in preprocessing: {testSummary}",
                        RefOutInSeverity.Error));
                }
            }

            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectRefOutInPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == RefOutInSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
            RequiredRefStructTypes = allRefStructTypes,
            BuildLogSummary = buildSummary,
            TestLogSummary = testSummary,
        };
    }

    private static async Task WriteExtensionFilesAsync(
        string intermediateRoot,
        string sourceRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles,
        ProjectGraph? projectGraph,
        IReadOnlyCollection<string> refStructTypes,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        ThrowOnCs2jRefOutInNameCollision(layout);

        await RefOutInSupportFiles.ReleaseAsync(
            Path.Combine(intermediateRoot, RefOutInSupportFiles.LibDirectoryName),
            refStructTypes);

        var hostProjectDirectories = ComputeExtensionHostProjectDirectories(
            layout, exclusionInfo, rewrittenFiles);
        EnsureExtensionProjectReferences(
            sourceRoot, intermediateRoot, layout, exclusionInfo,
            hostProjectDirectories, diagnostics);
    }

    private static void ThrowOnCs2jRefOutInNameCollision(ProjectPreprocessSourceLayout layout)
    {
        var collision = layout.ProjectFiles.FirstOrDefault(p =>
            Cs2jRefOutInProject.IsCs2jRefOutInProject(p) &&
            !IsOwnReleasedRefOutInProjectFile(p));
        if (collision != null)
        {
            throw new InvalidOperationException(
                $"The source solution already contains a project named " +
                $"'{Cs2jRefOutInProject.ProjectFileName}' ({collision}). That name is reserved " +
                "for the cs2j ref/out/in support library; rename the project and retry.");
        }
    }

    private static bool IsOwnReleasedRefOutInProjectFile(string projectFilePath)
    {
        try
        {
            var content = File.ReadAllText(projectFilePath);
            return string.Equals(
                NormalizeLineEndings(content),
                NormalizeLineEndings(RefOutInSupportFiles.GetProjectFileContent()),
                StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static List<string> FindOwnReleaseLibDirectories(string sourceRoot)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        return Directory
            .EnumerateDirectories(sourceRoot, RefOutInSupportFiles.LibDirectoryName, options)
            .Where(dir => IsOwnReleasedRefOutInProjectFile(
                Path.Combine(dir, Cs2jRefOutInProject.ProjectFileName)))
            .Select(Path.GetFullPath)
            .ToList();
    }

    private static bool IsUnderOwnReleaseLib(string filePath, IReadOnlyList<string> ownReleaseDirectories)
    {
        var fullPath = Path.GetFullPath(filePath);
        return ownReleaseDirectories.Any(dir =>
            fullPath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlySet<string> ComputeExtensionHostProjectDirectories(
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlyList<string> rewrittenFiles)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rewrittenFiles.Count == 0) return result;

        foreach (var projectFile in layout.ProjectFiles
                     .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;
            var projectDir = Path.GetDirectoryName(projectFile)!;
            if (rewrittenFiles.Any(f => ProjectPreprocessSource.IsSameOrDescendantPath(f, projectDir)))
                result.Add(projectDir);
        }
        return result;
    }

    private static void EnsureExtensionProjectReferences(
        string sourceRoot,
        string intermediateRoot,
        ProjectPreprocessSourceLayout layout,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        IReadOnlySet<string> hostProjectDirectories,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        var libProjectPath = Path.Combine(
            intermediateRoot,
            RefOutInSupportFiles.LibDirectoryName,
            Cs2jRefOutInProject.ProjectFileName);

        foreach (var projectFile in layout.ProjectFiles
                     .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (SupportedProjectFiltering.IsUnderExcludedProject(projectFile, exclusionInfo))
                continue;
            var projectDir = Path.GetDirectoryName(projectFile)!;
            if (!hostProjectDirectories.Contains(projectDir))
                continue;
            var destProjectFile = Path.Combine(
                intermediateRoot, Path.GetRelativePath(sourceRoot, projectFile));
            EnsureExtensionProjectReference(destProjectFile, libProjectPath, intermediateRoot, diagnostics);
        }
    }

    private static void EnsureExtensionProjectReference(
        string projectFilePath, string libProjectPath, string intermediateRoot,
        List<RefOutInRewriterDiagnostic> diagnostics)
    {
        if (!File.Exists(projectFilePath)) return;
        XDocument document;
        try { document = XDocument.Load(projectFilePath, LoadOptions.PreserveWhitespace); }
        catch { return; }
        var root = document.Root;
        if (root == null || !root.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            return;

        var projectDir = Path.GetDirectoryName(projectFilePath)!;
        var isSdkProject = root.Attribute("Sdk") != null;
        var enableDefaultCompileItems = GetPropertyValue(root, "EnableDefaultCompileItems");
        if (isSdkProject
            && !string.Equals(enableDefaultCompileItems?.Trim(), "false", StringComparison.OrdinalIgnoreCase)
            && PathsEqual(projectDir, intermediateRoot))
        {
            var removePattern = Path.Combine(RefOutInSupportFiles.LibDirectoryName, "**");
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

        foreach (var refEl in root.Descendants()
                     .Where(e => e.Name.LocalName == "ProjectReference").ToList())
        {
            var include = (string?)refEl.Attribute("Include");
            if (string.IsNullOrWhiteSpace(include)) continue;
            var resolved = Path.GetFullPath(Path.Combine(projectDir, include!));
            if (PathsEqual(resolved, libProjectPath))
                refEl.Remove();
        }

        var relativeInclude = Path.GetRelativePath(projectDir, libProjectPath);
        root.Add(
            new XText(Environment.NewLine + "  "),
            new XElement("ItemGroup",
                new XElement("ProjectReference", new XAttribute("Include", relativeInclude))),
            new XText(Environment.NewLine));
        document.Save(projectFilePath, SaveOptions.DisableFormatting);
    }

    private static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null) return false;
        return projectGraph.ProjectsInTopologicalOrder.Any(p =>
            p.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(p.ProjectFilePath)));
    }

    private static async Task<string> RunDotNetAsync(
        string verb, string targetPath, bool verbose, string? configuration = null)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"{verb} \"{targetPath}\" --nologo" +
                            (configuration is null ? "" : $" -c {configuration}") +
                            " -p:GenerateResourceUsePreserializedResources=true",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null) return "EXIT:-1";
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return $"EXIT:{process.ExitCode}\n{output}";
            return $"OK:{process.ExitCode}";
        }
        catch (Exception ex)
        {
            return $"EXIT:-1\n{ex.Message}";
        }
    }

    private static void DeleteBuildOutputDirectories(string root)
    {
        foreach (var binDir in Directory
                     .EnumerateDirectories(root, "bin", SearchOption.AllDirectories).ToList())
        {
            try { Directory.Delete(binDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd('\\', '/'),
            Path.GetFullPath(b).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetPropertyValue(XElement root, string propertyName) =>
        root.Descendants()
            .Where(e => e.Name.LocalName == propertyName)
            .Select(e => (string?)e.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static void MergeStatistics(
        RefOutInRewriterStatistics target,
        RefOutInRewriterStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.RefParamsRewritten += source.RefParamsRewritten;
        target.OutParamsRewritten += source.OutParamsRewritten;
        target.InByValueParams += source.InByValueParams;
        target.ScopedRemoved += source.ScopedRemoved;
        target.DelegatesRewritten += source.DelegatesRewritten;
        target.RefReturnsRewritten += source.RefReturnsRewritten;
        target.RefLocalsRewritten += source.RefLocalsRewritten;
        target.RefFieldsRewritten += source.RefFieldsRewritten;
        target.ArrayElementProxies += source.ArrayElementProxies;
        target.RefStructParamsRewritten += source.RefStructParamsRewritten;
        target.CallSitesLifted += source.CallSitesLifted;
        target.HoldersInjected += source.HoldersInjected;
        target.AccessorProxies += source.AccessorProxies;
        target.LockedContractMembers += source.LockedContractMembers;
        target.InOverloadKept += source.InOverloadKept;
        target.ExternalRefArgumentsKept += source.ExternalRefArgumentsKept;
    }
}
