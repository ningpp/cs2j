using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ArrayMethodRewriter;

namespace CSharpToJava.CLI;

internal sealed class ProjectArrayMethodPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public ArrayMethodRewriterOptions RewriterOptions { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
}

internal sealed class ProjectArrayMethodPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ArrayMethodRewriterStatistics Statistics { get; init; }
    public required IReadOnlyList<ArrayMethodRewriterDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="ArrayMethodPassAdapter"/>.
/// </summary>
internal static class ProjectArrayMethodPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-array-src";

    public static async Task<ProjectArrayMethodPreprocessResult> PreprocessAsync(
        ProjectArrayMethodPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new ArrayMethodPassAdapter(
            request.RewriterOptions,
            request.Verbose,
            request.FilterUnsupportedProjects,
            request.CompatNamespacePrefixes);

        var run = await ProjectPassRunner.RunAsync(
            adapter,
            new ProjectPassRunRequest
            {
                SourcePath = request.SourcePath,
                DestinationRoot = request.DestinationRoot,
                Force = request.Force,
            });

        return new ProjectArrayMethodPreprocessResult
        {
            OriginalSourcePath = run.OriginalSourcePath,
            SourceRoot = run.SourceRoot,
            IntermediateRoot = run.IntermediateRoot,
            PreprocessedSourcePath = run.PreprocessedSourcePath,
            Success = run.Success,
            Statistics = adapter.Statistics,
            Diagnostics = adapter.Diagnostics.ToList(),
        };
    }
}
