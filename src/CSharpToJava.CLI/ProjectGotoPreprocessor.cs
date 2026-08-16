using System.Diagnostics;
using CSharpToJava.Core.GotoEliminator;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.CLI;

internal sealed class ProjectGotoPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public bool Strict { get; init; }

    /// <summary>
    /// When true (default) the preprocessor first scans the project graph and only
    /// copies/transforms projects the converter supports, rewriting the copied .sln so
    /// unsupported (e.g. WPF/UWP/WinForms) projects are dropped.
    /// </summary>
    public bool FilterUnsupportedProjects { get; init; } = true;

    /// <summary>
    /// Explicit namespace mappings from the type-mapping config (e.g.
    /// System.Windows.Forms → dotnet.winforms.swing). Windows-only projects whose
    /// platform namespaces are covered are kept instead of excluded.
    /// </summary>
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectGotoPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required GotoEliminatorStatistics Statistics { get; init; }
    public required IReadOnlyList<GotoEliminatorDiagnostic> Diagnostics { get; init; }
}

internal static class ProjectGotoPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-no-goto-src";

    public static async Task<ProjectGotoPreprocessResult> PreprocessAsync(ProjectGotoPreprocessRequest request)
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
                    $"No-goto source directory already exists: {intermediateRoot}. Use --force to overwrite.");
            }

            Directory.Delete(intermediateRoot, recursive: true);
        }

        Directory.CreateDirectory(intermediateRoot);

        var diagnostics = new List<GotoEliminatorDiagnostic>();
        var statistics = new GotoEliminatorStatistics();
        var eliminator = new GotoEliminator();
        var options = new GotoEliminatorOptions(request.Verbose, request.Strict);
        var sourceRoot = layout.SourceRoot;
        var destinationRoot = Path.GetFullPath(request.DestinationRoot);

        // Strategy: before transforming, scan the project graph to determine which
        // projects the converter supports, then copy ONLY those projects (preserving the
        // directory structure) and rewrite the copied .sln to drop the unsupported ones.
        var projectGraph = ProjectPreprocessSource.TryLoadProjectGraph(layout, request.Verbose);
        var exclusionInfo = request.FilterUnsupportedProjects
            ? SupportedProjectFiltering.ComputeExclusions(
                projectGraph,
                request.Verbose,
                request.CompatNamespacePrefixes,
                layout.SourceRoot)
            : new SupportedProjectFiltering.ExclusionInfo();

        // Copy project files (.csproj, .sln) to the intermediate directory so that
        // SolutionLoader can discover the full project graph after preprocessing.
        await ProjectPreprocessSource.CopyProjectFilesAsync(layout, sourceRoot, intermediateRoot, exclusionInfo);
        ProjectPreprocessSource.CopyAncestorMsBuildDirectoryFiles(
            sourceRoot,
            intermediateRoot,
            "[no-goto]",
            request.Verbose);

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
                // The solution file was already copied (and rewritten to drop
                // unsupported projects) in the project-file loop above; copying it
                // again here would restore the unfiltered original.
                if (sourceFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Copy(sourceFile, destinationFile, overwrite: true);
                continue;
            }

            var sourceCode = await File.ReadAllTextAsync(sourceFile);
            var result = eliminator.Eliminate(sourceCode, options);
            MergeStatistics(statistics, result.Statistics);

            foreach (var diagnostic in result.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Message = $"{relativePath}: {diagnostic.Message}",
                });
            }

            if (!result.Changed)
            {
                File.Copy(sourceFile, destinationFile, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(destinationFile, result.OutputCode, new System.Text.UTF8Encoding(false));
            }

            var outputCode = result.Changed ? result.OutputCode : sourceCode;
            if (ContainsGotoOrLabel(outputCode))
            {
                diagnostics.Add(new GotoEliminatorDiagnostic(
                    GotoEliminatorSeverity.Error,
                    $"{relativePath}: goto/label syntax remains after preprocessing"));
            }
        }

        // Restore NuGet packages in the intermediate directory so that
        // MSBuildWorkspace can resolve package references when it opens the
        // preprocessed project. Without a restore, MetadataReferences is empty
        // and the semantic model is broken, which leads to incorrect code
        // generation (e.g. property accesses emitted as field accesses).
        var restoreTarget = ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
        if (restoreTarget != null)
        {
            if (request.Verbose)
            {
                Console.WriteLine($"Restoring packages: {restoreTarget}");
            }
            var restoreExitCode = ProjectPreprocessSource.RunDotNetRestore(restoreTarget, "[no-goto]");
            if (request.Verbose)
            {
                Console.WriteLine($"Restore exit code: {restoreExitCode}");
            }
            if (restoreExitCode != 0)
            {
                diagnostics.Add(new GotoEliminatorDiagnostic(
                    GotoEliminatorSeverity.Error,
                    $"dotnet restore failed with exit code {restoreExitCode} for {restoreTarget}. " +
                    "Package references may not be resolved, leading to incorrect code generation."));
            }
        }

        return new ProjectGotoPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = ProjectPreprocessSource.MapToIntermediate(layout, intermediateRoot),
            Success = diagnostics.Count == 0,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    internal static string? ResolveRestoreTarget(string intermediateRoot, ProjectPreprocessSourceLayout layout)
        => ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);

    private static bool ContainsGotoOrLabel(string code)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        return root.DescendantNodes().Any(node => node is GotoStatementSyntax or LabeledStatementSyntax);
    }

    private static void MergeStatistics(GotoEliminatorStatistics target, GotoEliminatorStatistics source)
    {
        target.FilesScanned += source.FilesScanned;
        target.FilesTransformed += source.FilesTransformed;
        target.FilesSkippedClean += source.FilesSkippedClean;
        target.MethodsTransformed += source.MethodsTransformed;
        target.MethodsSkipped += source.MethodsSkipped;
        target.GotosEliminated += source.GotosEliminated;
    }

}
