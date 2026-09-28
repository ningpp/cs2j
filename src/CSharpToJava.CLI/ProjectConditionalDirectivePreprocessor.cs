using CSharpToJava.Core.ConditionalDirectiveStrip;

namespace CSharpToJava.CLI;

internal sealed class ProjectConditionalDirectivePreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public bool Strict { get; init; }
    public string Configuration { get; init; } = "Release";
    public string? Tfm { get; init; }
    public bool RemoveRegions { get; init; }
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectConditionalDirectivePreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required int FilesTransformed { get; init; }
    public required int FilesSkippedClean { get; init; }
    public required int DirectivesRemoved { get; init; }
    public required int DisabledBranchesRemoved { get; init; }
    public required IReadOnlyList<string> Diagnostics { get; init; }
}

internal static class ProjectConditionalDirectivePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-conditional-src";

    public static async Task<ProjectConditionalDirectivePreprocessResult> PreprocessAsync(
        ProjectConditionalDirectivePreprocessRequest request)
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
                    $"Conditional source directory already exists: {intermediateRoot}. " +
                    "Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<string>();
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);
        var filesTransformed = 0;
        var filesSkippedClean = 0;
        var totalDirectivesRemoved = 0;
        var totalDisabledBranchesRemoved = 0;

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
            sourceRoot, intermediateRoot, "[conditional]", request.Verbose);

        foreach (var sourceFile in ProjectPreprocessSource.EnumerateSourceFiles(
                     sourceRoot,
                     intermediateRoot,
                     Path.GetFullPath(request.DestinationRoot))
                     .Where(file => !SupportedProjectFiltering.IsUnderExcludedProject(file, exclusionInfo)))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var symbols = ProjectSymbolResolver.ResolveForFile(
                sourceFile,
                request.Configuration,
                request.Tfm);

            foreach (var warning in symbols.Warnings)
            {
                diagnostics.Add($"[Warning] {relativePath}:0: {warning}");
            }

            var stripOptions = new ConditionalDirectiveStripOptions
            {
                RemoveRegions = request.RemoveRegions,
                Symbols = symbols.Symbols,
            };

            var result = new ConditionalDirectiveStripper().Strip(sourceCode, stripOptions);

            foreach (var diagnostic in result.Diagnostics)
            {
                var severity = diagnostic.Severity == ConditionalDirectiveSeverity.Error ? "Error" : "Warning";
                diagnostics.Add($"[{severity}] {relativePath}:{diagnostic.Line ?? 0}: {diagnostic.Message}");
            }

            if (result.Changed)
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode,
                    new System.Text.UTF8Encoding(false));
                filesTransformed++;
                totalDirectivesRemoved += result.Statistics.DirectivesRemoved;
                totalDisabledBranchesRemoved += result.Statistics.DisabledBranchesRemoved;

                if (request.Verbose)
                {
                    Console.WriteLine($"[conditional] transformed: {relativePath} " +
                        $"({result.Statistics.DirectivesRemoved} directives, " +
                        $"{result.Statistics.DisabledBranchesRemoved} disabled blocks)");
                }
            }
            else
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
                filesSkippedClean++;
                if (request.Verbose)
                {
                    Console.WriteLine($"[conditional] clean (copy): {relativePath}");
                }
            }
        }

        // Restore packages so MSBuildWorkspace can resolve references in later passes
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[conditional]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }
            if (restoreExitCode != 0)
            {
                diagnostics.Add($"[Warning] dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}.");
            }
        }

        var hasErrors = diagnostics.Any(d => d.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase));
        return new ProjectConditionalDirectivePreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = !hasErrors,
            FilesTransformed = filesTransformed,
            FilesSkippedClean = filesSkippedClean,
            DirectivesRemoved = totalDirectivesRemoved,
            DisabledBranchesRemoved = totalDisabledBranchesRemoved,
            Diagnostics = diagnostics,
        };
    }
}
