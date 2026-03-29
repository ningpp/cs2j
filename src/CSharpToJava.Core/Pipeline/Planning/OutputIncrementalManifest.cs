using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.Pipeline.Planning;

public enum OutputIncrementalEntryKind
{
    GeneratedSource,
    CopiedResource,
    BuildFile,
    WorkspaceManifest,
    PassProfile,
    CanarySummary,
    InputFingerprint,
}

public sealed class OutputIncrementalManifestEntry
{
    public required string RelativePath { get; init; }
    public required OutputIncrementalEntryKind Kind { get; init; }
    public required string ContentHash { get; init; }
}

public sealed class OutputIncrementalManifest
{
    public required string SourceName { get; init; }
    public string? SourcePath { get; init; }
    public required IReadOnlyList<OutputIncrementalManifestEntry> Entries { get; init; }
}

public sealed class OutputIncrementalManifestJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public string Serialize(OutputIncrementalManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions);
    }

    public OutputIncrementalManifest? Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<OutputIncrementalManifest>(json, SerializerOptions);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public sealed class OutputIncrementalWriteSession
{
    public const string ManifestFileName = "cs2j-output-manifest.json";

    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _destinationRoot;
    private readonly string _sourceName;
    private readonly string _sourcePath;
    private readonly Dictionary<string, OutputIncrementalManifestEntry> _previousEntries;
    private readonly Dictionary<string, OutputIncrementalManifestEntry> _currentEntries;

    private OutputIncrementalWriteSession(
        string destinationRoot,
        string sourceName,
        string sourcePath,
        IReadOnlyDictionary<string, OutputIncrementalManifestEntry> previousEntries,
        bool hasPreviousManifest)
    {
        _destinationRoot = destinationRoot;
        _sourceName = sourceName;
        _sourcePath = sourcePath;
        _previousEntries = new Dictionary<string, OutputIncrementalManifestEntry>(previousEntries, StringComparer.OrdinalIgnoreCase);
        _currentEntries = new Dictionary<string, OutputIncrementalManifestEntry>(StringComparer.OrdinalIgnoreCase);
        HasPreviousManifest = hasPreviousManifest;
    }

    public bool HasPreviousManifest { get; }

    public static OutputIncrementalWriteSession Create(string destinationRoot, string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var fullDestinationRoot = Path.GetFullPath(destinationRoot);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var manifestPath = Path.Combine(fullDestinationRoot, ManifestFileName);
        var serializer = new OutputIncrementalManifestJsonSerializer();

        if (!File.Exists(manifestPath))
        {
            return new OutputIncrementalWriteSession(
                fullDestinationRoot,
                GetSourceName(fullSourcePath),
                fullSourcePath,
                new Dictionary<string, OutputIncrementalManifestEntry>(StringComparer.OrdinalIgnoreCase),
                hasPreviousManifest: false);
        }

        try
        {
            var manifest = serializer.Deserialize(File.ReadAllText(manifestPath, Utf8WithoutBom));
            var previousEntries = manifest?.Entries.ToDictionary(
                entry => entry.RelativePath,
                entry => entry,
                StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, OutputIncrementalManifestEntry>(StringComparer.OrdinalIgnoreCase);

            return new OutputIncrementalWriteSession(
                fullDestinationRoot,
                manifest?.SourceName ?? GetSourceName(fullSourcePath),
                fullSourcePath,
                previousEntries,
                hasPreviousManifest: true);
        }
        catch
        {
            return new OutputIncrementalWriteSession(
                fullDestinationRoot,
                GetSourceName(fullSourcePath),
                fullSourcePath,
                new Dictionary<string, OutputIncrementalManifestEntry>(StringComparer.OrdinalIgnoreCase),
                hasPreviousManifest: false);
        }
    }

    public bool WriteTextFile(string outputPath, string content, OutputIncrementalEntryKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(content);

        var normalizedOutputPath = NormalizeOutputPath(outputPath);
        var contentHash = ComputeHash(Utf8WithoutBom.GetBytes(content));
        RegisterCurrentEntry(normalizedOutputPath, kind, contentHash);

        if (CanReuseExistingFile(normalizedOutputPath, contentHash) && File.Exists(normalizedOutputPath))
        {
            return false;
        }

        var outputDir = Path.GetDirectoryName(normalizedOutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.WriteAllText(normalizedOutputPath, content, Utf8WithoutBom);
        return true;
    }

    public bool CopyFile(string sourcePath, string outputPath, OutputIncrementalEntryKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var normalizedSourcePath = Path.GetFullPath(sourcePath);
        var normalizedOutputPath = NormalizeOutputPath(outputPath);
        var contentHash = ComputeHash(normalizedSourcePath);
        RegisterCurrentEntry(normalizedOutputPath, kind, contentHash);

        if (CanReuseExistingFile(normalizedOutputPath, contentHash) && File.Exists(normalizedOutputPath))
        {
            return false;
        }

        var outputDir = Path.GetDirectoryName(normalizedOutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        File.Copy(normalizedSourcePath, normalizedOutputPath, overwrite: true);
        return true;
    }

    public async Task SaveAsync()
    {
        DeleteStaleFiles();
        DeleteEmptyDirectories();

        Directory.CreateDirectory(_destinationRoot);
        var serializer = new OutputIncrementalManifestJsonSerializer();
        var manifest = new OutputIncrementalManifest
        {
            SourceName = _sourceName,
            SourcePath = _sourcePath,
            Entries = _currentEntries.Values
                .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        var manifestPath = Path.Combine(_destinationRoot, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, serializer.Serialize(manifest), Utf8WithoutBom);
    }

    private void RegisterCurrentEntry(string outputPath, OutputIncrementalEntryKind kind, string contentHash)
    {
        _currentEntries[GetRelativePath(outputPath)] = new OutputIncrementalManifestEntry
        {
            RelativePath = GetRelativePath(outputPath),
            Kind = kind,
            ContentHash = contentHash,
        };
    }

    private bool CanReuseExistingFile(string outputPath, string contentHash)
    {
        return _previousEntries.TryGetValue(GetRelativePath(outputPath), out var previousEntry)
            && string.Equals(previousEntry.ContentHash, contentHash, StringComparison.Ordinal);
    }

    private void DeleteStaleFiles()
    {
        foreach (var previousEntry in _previousEntries.Values)
        {
            if (_currentEntries.ContainsKey(previousEntry.RelativePath))
            {
                continue;
            }

            var outputPath = Path.Combine(_destinationRoot, previousEntry.RelativePath);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private void DeleteEmptyDirectories()
    {
        if (!Directory.Exists(_destinationRoot))
        {
            return;
        }

        var directories = Directory.GetDirectories(_destinationRoot, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length)
            .ToList();

        foreach (var directory in directories)
        {
            if (string.Equals(Path.GetFullPath(directory), _destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: false);
            }
        }
    }

    private string NormalizeOutputPath(string outputPath)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        var destinationRootWithSeparator = _destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullOutputPath.StartsWith(destinationRootWithSeparator, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullOutputPath, _destinationRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Output path '{outputPath}' is outside destination root '{_destinationRoot}'.");
        }

        return fullOutputPath;
    }

    private string GetRelativePath(string outputPath)
    {
        var relativePath = Path.GetRelativePath(_destinationRoot, outputPath);
        return relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }

    private static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static string GetSourceName(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            return Path.GetFileNameWithoutExtension(sourcePath);
        }

        var trimmed = sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }
}