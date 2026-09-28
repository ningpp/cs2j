using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ReadOnlyStructMaker;

namespace CSharpToJava.CLI;

internal sealed class ProjectReadOnlyPreprocessRequest
{
    public required string SourcePath { get; init; }
    public required string DestinationRoot { get; init; }
    public bool Force { get; init; } = true;
    public bool Verbose { get; init; }
    public ReadOnlyStructMakerOptions MakerOptions { get; init; } = new();

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

    /// <summary>Run <c>dotnet build</c> on the readonly output to verify it compiles.</summary>
    public bool VerifyBuild { get; init; } = true;

    /// <summary>Run <c>dotnet test</c> on the readonly output (only when test projects exist).</summary>
    public bool VerifyTests { get; init; } = true;
}

internal sealed class ProjectReadOnlyPreprocessResult
{
    public required string OriginalSourcePath { get; init; }
    public required string SourceRoot { get; init; }
    public required string IntermediateRoot { get; init; }
    public required string PreprocessedSourcePath { get; init; }
    public required bool Success { get; init; }
    public required ReadOnlyStructMakerStatistics Statistics { get; init; }
    public required IReadOnlyList<ReadOnlyStructMakerDiagnostic> Diagnostics { get; init; }
}

/// <summary>
/// Thin compatibility facade over the unified <see cref="ProjectPassRunner"/> +
/// <see cref="MakeReadonlyPassAdapter"/>. The multi-phase engine lives in
/// <see cref="ReadOnlyStructPassEngine"/>.
/// </summary>
internal static class ProjectReadOnlyStructPreprocessor
{
    public const string IntermediateDirectoryName = ".cs2j-readonly-src";

    public static async Task<ProjectReadOnlyPreprocessResult> PreprocessAsync(ProjectReadOnlyPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var adapter = new MakeReadonlyPassAdapter(
            request.MakerOptions,
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

        return new ProjectReadOnlyPreprocessResult
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

    /// <summary>
    /// Second pass: updates call sites of migrated void→struct methods across all
    /// files. Retained as a compatibility entry point invoked directly by tests; the
    /// <paramref name="request"/> argument is part of the historical signature and is
    /// unused (gating is decided by the caller/orchestrator).
    /// </summary>
    internal static Task UpdateCrossFileCallSites(
        string intermediateRoot, string sourceRoot, List<string> sourceFiles,
        Dictionary<string, string> migratedStructs, ProjectReadOnlyPreprocessRequest request)
        => ReadOnlyStructPassEngine.UpdateCrossFileCallSites(
            intermediateRoot, sourceRoot, sourceFiles, migratedStructs);
}
