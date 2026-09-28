using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.LocalFunctionLift;
using CoreLocalFunctionLiftOptions = CSharpToJava.Core.LocalFunctionLift.LocalFunctionLiftOptions;

namespace CSharpToJava.CLI;

internal sealed class ProjectLocalFunctionLiftRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public CoreLocalFunctionLiftOptions Options { get; init; } = new();
    public bool FilterUnsupportedProjects { get; init; } = true;
    public IReadOnlyCollection<string>? CompatNamespacePrefixes { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool VerifyTests { get; init; } = true;
    public bool Strict { get; init; } = true;
}

internal sealed class ProjectLocalFunctionLiftResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required LocalFunctionLiftStatistics Statistics { get; init; }
    public required IReadOnlyList<LocalFunctionLiftDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="LocalFunctionLiftPassAdapter"/>.
/// </summary>
internal static class ProjectLocalFunctionLiftPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-local-fn-src";

    public static async Task<ProjectLocalFunctionLiftResult> PreprocessAsync(
        ProjectLocalFunctionLiftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new LocalFunctionLiftPassAdapter(
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

        return new ProjectLocalFunctionLiftResult
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
