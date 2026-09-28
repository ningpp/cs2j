using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;

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

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="ConditionalPassAdapter"/>.
/// </summary>
internal static class ProjectConditionalDirectivePreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-conditional-src";

    public static async Task<ProjectConditionalDirectivePreprocessResult> PreprocessAsync(
        ProjectConditionalDirectivePreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new ConditionalPassAdapter(
            request.Verbose,
            request.FilterUnsupportedProjects,
            request.CompatNamespacePrefixes,
            request.Configuration,
            request.Tfm,
            request.RemoveRegions);

        var run = await ProjectPassRunner.RunAsync(
            adapter,
            new ProjectPassRunRequest
            {
                SourcePath = request.SourcePath,
                DestinationRoot = request.DestinationRoot,
                Force = request.Force,
            });

        return new ProjectConditionalDirectivePreprocessResult
        {
            OriginalSourcePath = run.OriginalSourcePath,
            SourceRoot = run.SourceRoot,
            IntermediateRoot = run.IntermediateRoot,
            PreprocessedSourcePath = run.PreprocessedSourcePath,
            Success = run.Success,
            FilesTransformed = adapter.FilesTransformed,
            FilesSkippedClean = adapter.FilesSkippedClean,
            DirectivesRemoved = adapter.DirectivesRemoved,
            DisabledBranchesRemoved = adapter.DisabledBranchesRemoved,
            Diagnostics = adapter.Diagnostics.ToList(),
        };
    }
}
