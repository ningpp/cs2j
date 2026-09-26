using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectNullCoalesceRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public NullCoalesceOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectNullCoalesceResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required NullCoalesceStatistics Statistics { get; init; }
    public required IReadOnlyList<NullCoalesceDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectNullCoalescePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-null-coalesce-src";

    public static async Task<ProjectNullCoalesceResult> PreprocessAsync(
        ProjectNullCoalesceRequest request)
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
                    $"Null-coalesce source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }
            Directory.Delete(intermediateRoot, recursive: true);
        }
        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<NullCoalesceDiagnostic>();
        var statistics = new NullCoalesceStatistics();
        var rewriter = new NullCoalesceRewriter();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph, request.Verbose,
                request.CompatNamespacePrefixes, layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        await ProjectPreprocessSource.CopyProjectFilesAsync(
            layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot, intermediateRoot, "[null-coalesce]", request.Verbose);

        var sourceFiles = ProjectPreprocessSource.EnumerateSourceFiles(
                sourceRoot, intermediateRoot,
                Path.GetFullPath(request.DestinationRoot))
            .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo))
            .ToList();
        var csFiles = sourceFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Pre-parse all .cs files for SemanticModel
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
            "null-coalesce-preprocess",
            syntaxTrees.Values,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true));

        // Process each .cs file
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

            if (!syntaxTrees.TryGetValue(sourceFile, out var tree))
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var semanticModel = compilation.GetSemanticModel(tree);
            var result = rewriter.Preprocess(sourceCode, semanticModel, request.Options);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with { FilePath = relativePath });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode,
                    new System.Text.UTF8Encoding(false));
            }

            if (request.Verbose)
            {
                Console.WriteLine(
                    $"[null-coalesce] {(result.Changed ? "transformed" : "clean (copy)")}: {relativePath}");
            }
        }

        // Residual detection
        var residualCount = CountResidualNullCoalesce(intermediateRoot);
        statistics.ResidualCount = residualCount;

        if (residualCount > 0)
        {
            var severity = request.Strict
                ? NullCoalesceSeverity.Error
                : NullCoalesceSeverity.Warning;
            diagnostics.Add(new NullCoalesceDiagnostic(
                severity,
                $"{residualCount} residual ??/??= operator(s) remaining after preprocessing."));
        }

        // Restore + verify build/tests
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[null-coalesce]");
            if (request.Verbose)
                Console.WriteLine($"Restore exit code: {restoreExitCode}");

            if (restoreExitCode != 0)
            {
                diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Warning,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}."));
            }

            await VerifyBuildAndTestsAsync(restoreTarget, projectGraph, exclusionInfo,
                request, diagnostics);
            DeleteBuildOutputDirectories(intermediateRoot);
        }

        return new ProjectNullCoalesceResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !diagnostics.Any(d => d.Severity == NullCoalesceSeverity.Error),
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static int CountResidualNullCoalesce(string intermediateRoot)
    {
        var count = 0;
        foreach (var csFile in Directory.EnumerateFiles(
            intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(csFile);
            var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(content).GetRoot();
            count += root.DescendantNodes().Count(n =>
                n is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.CoalesceExpression)
                || n is AssignmentExpressionSyntax a
                    && a.IsKind(SyntaxKind.CoalesceAssignmentExpression));
        }
        return count;
    }

    private static async Task VerifyBuildAndTestsAsync(
        string restoreTarget,
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo,
        ProjectNullCoalesceRequest request,
        List<NullCoalesceDiagnostic> diagnostics)
    {
        if (!request.VerifyBuild && !request.VerifyTests)
            return;

        Console.WriteLine($"[null-coalesce] Verifying build: dotnet build \"{restoreTarget}\" -c Release");
        var buildExit = await RunDotNetCommandAsync("build", restoreTarget, request.Verbose, "Release");
        if (buildExit != 0)
        {
            diagnostics.Add(new NullCoalesceDiagnostic(
                NullCoalesceSeverity.Error,
                $"dotnet build failed with exit code {buildExit} after null-coalesce preprocessing."));
            return;
        }

        if (request.VerifyTests && HasSupportedTestProjects(projectGraph, exclusionInfo))
        {
            Console.WriteLine($"[null-coalesce] Verifying tests: dotnet test \"{restoreTarget}\" -c Release");
            var testExit = await RunDotNetCommandAsync("test", restoreTarget, request.Verbose, "Release");
            if (testExit != 0)
            {
                diagnostics.Add(new NullCoalesceDiagnostic(
                    NullCoalesceSeverity.Error,
                    $"dotnet test failed with exit code {testExit} after null-coalesce preprocessing."));
            }
        }
    }

    private static bool HasSupportedTestProjects(
        ProjectGraph? projectGraph,
        SupportedProjectFiltering.ExclusionInfo exclusionInfo)
    {
        if (projectGraph == null)
            return false;
        return projectGraph.ProjectsInTopologicalOrder.Any(project =>
            project.Kind == ProjectKind.Test &&
            !exclusionInfo.ExcludedProjectPaths.Contains(Path.GetFullPath(project.ProjectFilePath)));
    }

    private static async Task ForwardOutputAsync(System.IO.StreamReader source, TextWriter destination)
    {
        string? line;
        while ((line = await source.ReadLineAsync()) != null)
            await destination.WriteLineAsync(line);
    }

    private static async Task<int> RunDotNetCommandAsync(
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
            if (process == null)
            {
                Console.Error.WriteLine($"Failed to start dotnet {verb} process.");
                return -1;
            }
            var outputTask = ForwardOutputAsync(process.StandardOutput, Console.Out);
            var errorTask = ForwardOutputAsync(process.StandardError, Console.Error);
            await process.WaitForExitAsync();
            await outputTask;
            await errorTask;
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dotnet {verb} failed: {ex.Message}");
            return -1;
        }
    }

    private static void DeleteBuildOutputDirectories(string root)
    {
        foreach (var binDirectory in Directory
                     .EnumerateDirectories(root, "bin", SearchOption.AllDirectories)
                     .ToList())
        {
            try { Directory.Delete(binDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void MergeStatistics(
        NullCoalesceStatistics target,
        NullCoalesceStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.NullCoalescingRewritten += source.NullCoalescingRewritten;
        target.NullCoalescingAssignRewritten += source.NullCoalescingAssignRewritten;
        target.TempVariablesGenerated += source.TempVariablesGenerated;
        target.NestedRewrites += source.NestedRewrites;
        target.SkippedUnsupported += source.SkippedUnsupported;
        target.ResidualCount += source.ResidualCount;
    }
}
