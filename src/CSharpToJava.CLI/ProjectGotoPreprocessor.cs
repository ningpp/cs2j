using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.GotoEliminator;

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

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="GotoPassAdapter"/>.
/// </summary>
internal static class ProjectGotoPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-no-goto-src";

    public static async Task<ProjectGotoPreprocessResult> PreprocessAsync(ProjectGotoPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new GotoPassAdapter(
            request.Verbose,
            request.Strict,
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

        return new ProjectGotoPreprocessResult
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

    internal static string? ResolveRestoreTarget(string intermediateRoot, ProjectPreprocessSourceLayout layout)
        => ProjectPreprocessSource.ResolveRestoreTarget(layout, intermediateRoot);
}
