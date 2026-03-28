using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Workspace;

/// <summary>
/// Represents a resolved project within a workspace, including its compilation
/// and source documents. Replaces the manual ProjectDiscovery approach with
/// MSBuild-resolved references for complete semantic models.
/// </summary>
public sealed class WorkspaceProject
{
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required string Directory { get; init; }
    public required CSharpCompilation Compilation { get; init; }
    public required IReadOnlyList<Document> Documents { get; init; }
    public required IReadOnlyList<string> ProjectReferences { get; init; }
    public required bool IsTestProject { get; init; }
}
