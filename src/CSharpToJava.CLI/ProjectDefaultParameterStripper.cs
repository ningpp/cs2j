using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.DefaultParameterStripper;

namespace CSharpToJava.CLI;

internal sealed class ProjectDefaultParameterStripRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public DefaultParameterStripperOptions RewriterOptions { get; init; } = new();

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

    /// <summary>Run <c>dotnet build</c> on the strip-default output to verify it compiles.</summary>
    public bool VerifyBuild { get; init; } = true;

    /// <summary>Run <c>dotnet test</c> on the strip-default output (only when test projects exist).</summary>
    public bool VerifyTests { get; init; } = true;
}

internal sealed class ProjectDefaultParameterStripResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required DefaultParameterStripperStatistics Statistics { get; init; }
    public required IReadOnlyList<DefaultParameterStripDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="StripDefaultPassAdapter"/>.
/// </summary>
internal static class ProjectDefaultParameterStripper
{
    public const string IntermediateDirectoryName = ".cs2j-strip-default-src";

    public static async Task<ProjectDefaultParameterStripResult> PreprocessAsync(ProjectDefaultParameterStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new StripDefaultPassAdapter(
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

        return new ProjectDefaultParameterStripResult
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
