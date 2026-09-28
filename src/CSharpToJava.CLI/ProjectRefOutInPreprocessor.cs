using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.RefOutInPreprocessor;

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

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="RefOutInPassAdapter"/>.
/// </summary>
internal static class ProjectRefOutInPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-refoutin-src";

    public static async Task<ProjectRefOutInPreprocessResult> PreprocessAsync(
        ProjectRefOutInPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new RefOutInPassAdapter(
            request.RewriterOptions,
            request.Verbose,
            request.FilterUnsupportedProjects,
            request.CompatNamespacePrefixes,
            request.VerifyBuild,
            request.VerifyTests);

        var run = await ProjectPassRunner.RunAsync(
            adapter,
            new ProjectPassRunRequest
            {
                SourcePath = request.SourcePath,
                DestinationRoot = request.DestinationRoot,
                Force = request.Force,
            });

        return new ProjectRefOutInPreprocessResult
        {
            OriginalSourcePath = run.OriginalSourcePath,
            SourceRoot = run.SourceRoot,
            IntermediateRoot = run.IntermediateRoot,
            PreprocessedSourcePath = run.PreprocessedSourcePath,
            Success = run.Success,
            Statistics = adapter.Statistics,
            Diagnostics = adapter.Diagnostics.ToList(),
            RequiredRefStructTypes = adapter.RequiredRefStructTypes,
            BuildLogSummary = adapter.BuildLogSummary,
            TestLogSummary = adapter.TestLogSummary,
        };
    }
}
