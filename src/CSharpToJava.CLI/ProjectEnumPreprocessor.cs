using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.EnumPreprocessor;

namespace CSharpToJava.CLI;

internal sealed class ProjectEnumPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public EnumPreprocessorOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectEnumPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required EnumPreprocessorStatistics Statistics { get; init; }
    public required IReadOnlyList<EnumPreprocessorDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="EnumPassAdapter"/>. The historical request/result shape is preserved
/// for existing callers (convert-project, the enum verb, and project tests).
/// </summary>
internal static class ProjectEnumPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-enum-src";

    public static async Task<ProjectEnumPreprocessResult> PreprocessAsync(
        ProjectEnumPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new EnumPassAdapter(
            request.Options,
            request.Verbose,
            request.FilterUnsupportedProjects,
            request.CompatNamespacePrefixes,
            request.VerifyBuild,
            request.VerifyTests,
            request.Strict);

        var run = await ProjectPassRunner.RunAsync(
            adapter,
            new ProjectPassRunRequest
            {
                SourcePath = request.SourcePath,
                DestinationRoot = request.DestinationRoot,
                Force = request.Force,
            });

        return new ProjectEnumPreprocessResult
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
