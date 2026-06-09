using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.Pipeline;

/// <summary>
/// Identifies how a <see cref="Cs2jLibrary"/> was constructed.
/// Phase 1 only distinguishes between single-file, source-set, and pre-built compilation inputs.
/// </summary>
public enum Cs2jLibraryInputKind
{
    SingleFile,
    SourceSet,
    Compilation,
}

/// <summary>
/// Top-level batch object for a conversion run.
/// Mirrors J2CL's Library concept, but remains Java-only and Roslyn-centric.
/// </summary>
public sealed class Cs2jLibrary : IDisposable
{
    private readonly Action? _disposeAction;

    internal Cs2jLibrary(Action? disposeAction = null)
    {
        _disposeAction = disposeAction;
    }

    public required string Name { get; init; }
    public Cs2jLibraryInputKind InputKind { get; init; }
    public IReadOnlyList<Cs2jLibraryProject> Projects { get; init; } = [];
    public IReadOnlyList<Cs2jLibraryDocument> Documents { get; init; } = [];

    public bool IsSingleProject => Projects.Count == 1;
    public bool IsSingleDocument => Documents.Count == 1;
    public bool IsEmpty => Documents.Count == 0;
    public CSharpCompilation? PrimaryCompilation => Projects.FirstOrDefault()?.Compilation;

    /// <summary>
    /// Cross-project extension method index built during pre-conversion scanning.
    /// Populated before per-project conversion begins; consumed by transformers and validation passes.
    /// </summary>
    public ExtensionMethodIndex ExtensionMethodIndex { get; set; } = ExtensionMethodIndex.Empty;

    public void Dispose() => _disposeAction?.Invoke();
}

/// <summary>
/// Project-level view within a <see cref="Cs2jLibrary"/>.
/// </summary>
public sealed class Cs2jLibraryProject
{
    public required string Name { get; init; }
    public string? ProjectFilePath { get; init; }
    public required string ProjectDirectory { get; init; }
    public required CSharpCompilation Compilation { get; init; }
    public IReadOnlyList<Cs2jLibraryDocument> Documents { get; init; } = [];
    public IReadOnlyList<string> ProjectReferences { get; init; } = [];
    public bool IsTestProject { get; init; }
}

/// <summary>
/// Document-level view within a <see cref="Cs2jLibrary"/>.
/// </summary>
public sealed class Cs2jLibraryDocument
{
    public required string FilePath { get; init; }
    public string? ProjectName { get; init; }
    public string? Content { get; init; }
    public SyntaxTree? SyntaxTree { get; init; }
}

/// <summary>
/// Factory helpers for constructing <see cref="Cs2jLibrary"/> instances from existing inputs.
/// </summary>
public static class Cs2jLibraryFactory
{
    public static Cs2jLibrary CreateSingleFile(
        string sourceCode,
        string? filePath,
        SyntaxTree syntaxTree,
        CSharpCompilation compilation,
        string? libraryName = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(syntaxTree);
        ArgumentNullException.ThrowIfNull(compilation);

        var normalizedFilePath = NormalizePath(filePath, "Input.cs");
        var projectName = InferName(filePath, compilation.AssemblyName, "single-file");
        var projectDirectory = DetermineProjectDirectory(filePath, normalizedFilePath);

        var document = new Cs2jLibraryDocument
        {
            FilePath = normalizedFilePath,
            ProjectName = projectName,
            Content = sourceCode,
            SyntaxTree = syntaxTree,
        };

        var project = new Cs2jLibraryProject
        {
            Name = projectName,
            ProjectDirectory = projectDirectory,
            Compilation = compilation,
            Documents = [document],
        };

        return new Cs2jLibrary
        {
            Name = libraryName ?? projectName,
            InputKind = Cs2jLibraryInputKind.SingleFile,
            Projects = [project],
            Documents = [document],
        };
    }

    public static Cs2jLibrary CreateFromSourceFiles(
        IEnumerable<SourceFile> sourceFiles,
        CSharpCompilation compilation,
        string? libraryName = null,
        string? projectName = null,
        string? projectFilePath = null,
        IReadOnlyList<string>? projectReferences = null,
        bool isTestProject = false)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(compilation);

        var sourceFileList = sourceFiles.ToList();
        var syntaxTreeLookup = BuildSyntaxTreeLookup(compilation);
        var documents = new List<Cs2jLibraryDocument>(sourceFileList.Count);

        foreach (var sourceFile in sourceFileList)
        {
            var normalizedPath = NormalizePath(sourceFile.FilePath, "Source.cs");
            syntaxTreeLookup.TryGetValue(normalizedPath, out var syntaxTree);
            documents.Add(new Cs2jLibraryDocument
            {
                FilePath = normalizedPath,
                Content = sourceFile.Content,
                SyntaxTree = syntaxTree,
            });
        }

        var projectDirectory = DetermineProjectDirectory(projectFilePath, sourceFileList.Select(f => f.FilePath));
        var effectiveProjectName = projectName ?? InferName(projectDirectory, compilation.AssemblyName, "source-set");

        var project = new Cs2jLibraryProject
        {
            Name = effectiveProjectName,
            ProjectFilePath = NormalizeOptionalProjectFilePath(projectFilePath),
            ProjectDirectory = projectDirectory,
            Compilation = compilation,
            Documents = documents,
            ProjectReferences = projectReferences ?? [],
            IsTestProject = isTestProject,
        };

        return new Cs2jLibrary
        {
            Name = libraryName ?? effectiveProjectName,
            InputKind = Cs2jLibraryInputKind.SourceSet,
            Projects = [project],
            Documents = documents,
        };
    }

    public static Cs2jLibrary CreateFromCompilation(
        CSharpCompilation compilation,
        string? projectName = null,
        string? projectFilePath = null,
        IReadOnlyList<string>? projectReferences = null,
        bool isTestProject = false,
        string? libraryName = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var effectiveProjectName = projectName ?? InferName(projectFilePath, compilation.AssemblyName, "compilation");
        var documents = compilation.SyntaxTrees
            .Where(IsMaterialSyntaxTree)
            .Select(tree => new Cs2jLibraryDocument
            {
                FilePath = NormalizePath(tree.FilePath, "Source.cs"),
                ProjectName = effectiveProjectName,
                SyntaxTree = tree,
            })
            .ToList();

        var projectDirectory = DetermineProjectDirectory(projectFilePath, documents.Select(d => d.FilePath));
        var project = new Cs2jLibraryProject
        {
            Name = effectiveProjectName,
            ProjectFilePath = NormalizeOptionalProjectFilePath(projectFilePath),
            ProjectDirectory = projectDirectory,
            Compilation = compilation,
            Documents = documents,
            ProjectReferences = projectReferences ?? [],
            IsTestProject = isTestProject,
        };

        return new Cs2jLibrary
        {
            Name = libraryName ?? effectiveProjectName,
            InputKind = Cs2jLibraryInputKind.Compilation,
            Projects = [project],
            Documents = documents,
        };
    }

    private static Dictionary<string, SyntaxTree> BuildSyntaxTreeLookup(CSharpCompilation compilation)
    {
        var lookup = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        foreach (var syntaxTree in compilation.SyntaxTrees.Where(IsMaterialSyntaxTree))
        {
            lookup[NormalizePath(syntaxTree.FilePath, "Source.cs")] = syntaxTree;
        }

        return lookup;
    }

    private static bool IsMaterialSyntaxTree(SyntaxTree syntaxTree)
    {
        if (string.IsNullOrWhiteSpace(syntaxTree.FilePath))
        {
            return false;
        }

        return !syntaxTree.FilePath.StartsWith("<", StringComparison.Ordinal);
    }

    private static string NormalizeOptionalProjectFilePath(string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(projectFilePath)
            ? Path.GetFullPath(projectFilePath)
            : projectFilePath;
    }

    private static string NormalizePath(string? filePath, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return $"<{fallbackName}>";
        }

        if (filePath.StartsWith("<", StringComparison.Ordinal))
        {
            return filePath;
        }

        return Path.IsPathRooted(filePath)
            ? Path.GetFullPath(filePath)
            : filePath;
    }

    private static string DetermineProjectDirectory(string? projectFilePath, string normalizedDocumentPath)
    {
        if (!string.IsNullOrWhiteSpace(projectFilePath))
        {
            return DetermineProjectDirectory(projectFilePath, []);
        }

        if (normalizedDocumentPath.StartsWith("<", StringComparison.Ordinal))
        {
            return Directory.GetCurrentDirectory();
        }

        return Path.GetDirectoryName(normalizedDocumentPath) ?? Directory.GetCurrentDirectory();
    }

    private static string DetermineProjectDirectory(string? preferredPath, IEnumerable<string> fallbackPaths)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var normalizedPreferred = NormalizeOptionalProjectFilePath(preferredPath);
            var preferredDirectory = normalizedPreferred.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || normalizedPreferred.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(normalizedPreferred)
                : normalizedPreferred;

            if (!string.IsNullOrWhiteSpace(preferredDirectory))
            {
                return preferredDirectory;
            }
        }

        foreach (var path in fallbackPaths)
        {
            var normalizedPath = NormalizePath(path, "Source.cs");
            if (normalizedPath.StartsWith("<", StringComparison.Ordinal))
            {
                continue;
            }

            var directory = Path.GetDirectoryName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    private static string InferName(string? primaryPath, string? secondaryName, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            var trimmed = primaryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fileName = Path.GetFileNameWithoutExtension(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        if (!string.IsNullOrWhiteSpace(secondaryName))
        {
            return secondaryName;
        }

        return fallbackName;
    }
}
