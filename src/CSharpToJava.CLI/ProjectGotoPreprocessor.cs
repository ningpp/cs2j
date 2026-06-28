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

    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".vs",
        ".idea",
        "bin",
        "obj",
        "node_modules",
    };

    public static async Task<ProjectGotoPreprocessResult> PreprocessAsync(ProjectGotoPreprocessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationRoot);

        var layout = ResolveSourceLayout(request.SourcePath);
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

        foreach (var sourceFile in EnumerateSourceFiles(sourceRoot, destinationRoot))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var destinationFile = Path.Combine(intermediateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);

            if (!sourceFile.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
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

        return new ProjectGotoPreprocessResult
        {
            OriginalSourcePath = layout.EntryPath,
            SourceRoot = sourceRoot,
            IntermediateRoot = intermediateRoot,
            PreprocessedSourcePath = MapToIntermediate(layout, intermediateRoot),
            Success = diagnostics.Count == 0,
            Statistics = statistics,
            Diagnostics = diagnostics,
        };
    }

    private static IEnumerable<string> EnumerateSourceFiles(string sourceRoot, string destinationRoot)
    {
        var pending = new Stack<string>();
        pending.Push(sourceRoot);

        while (pending.Count > 0)
        {
            var currentDirectory = pending.Pop();
            if (IsSameOrDescendantPath(currentDirectory, destinationRoot))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                if (IgnoredDirectoryNames.Contains(Path.GetFileName(subDirectory)))
                {
                    continue;
                }

                if (IsSameOrDescendantPath(subDirectory, destinationRoot))
                {
                    continue;
                }

                pending.Push(Path.GetFullPath(subDirectory));
            }
        }
    }

    private static ProjectGotoSourceLayout ResolveSourceLayout(string sourcePath)
    {
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (Directory.Exists(fullSourcePath))
        {
            return new ProjectGotoSourceLayout(fullSourcePath, fullSourcePath);
        }

        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Source path not found.", fullSourcePath);
        }

        var sourceRoot = Path.GetDirectoryName(fullSourcePath)
            ?? throw new InvalidOperationException($"Cannot resolve source directory for {fullSourcePath}.");

        var extension = Path.GetExtension(fullSourcePath);
        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var graph = ProjectDiscovery.LoadProjectGraph(fullSourcePath);
                sourceRoot = GetCommonRoot(
                    graph.ProjectsInTopologicalOrder
                        .Select(project => project.ProjectDirectory)
                        .Append(sourceRoot));
            }
            catch
            {
                sourceRoot = Path.GetDirectoryName(fullSourcePath) ?? sourceRoot;
            }
        }

        return new ProjectGotoSourceLayout(sourceRoot, fullSourcePath);
    }

    private static string MapToIntermediate(ProjectGotoSourceLayout layout, string intermediateRoot)
    {
        if (string.Equals(layout.SourceRoot, layout.EntryPath, StringComparison.OrdinalIgnoreCase))
        {
            return intermediateRoot;
        }

        return Path.Combine(intermediateRoot, Path.GetRelativePath(layout.SourceRoot, layout.EntryPath));
    }

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

    private static string GetCommonRoot(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return Path.GetFullPath(".");
        }

        var commonRoot = normalized[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (normalized.Any(path => !IsSameOrDescendantPath(path, commonRoot)))
        {
            var parent = Directory.GetParent(commonRoot);
            if (parent == null)
            {
                break;
            }

            commonRoot = parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return commonRoot;
    }

    private static bool IsSameOrDescendantPath(string path, string candidateAncestor)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullAncestor = Path.GetFullPath(candidateAncestor).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(fullPath, fullAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullAncestor + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectGotoSourceLayout(string SourceRoot, string EntryPath);
}
