using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.Pipeline.Planning;

public enum InputFingerprintEntryKind
{
    SourceInput,
    MappingConfiguration,
    TemplateAsset,
    ToolAssembly,
}

public sealed class InputFingerprintEntry
{
    public required string Path { get; init; }
    public required InputFingerprintEntryKind Kind { get; init; }
    public required string ContentHash { get; init; }
}

public sealed class InputFingerprintBuildRequest
{
    public required string SourceName { get; init; }
    public required string SourcePath { get; init; }
    public IReadOnlyList<string> InputFilePaths { get; init; } = [];
    public IReadOnlyList<string> OptionTokens { get; init; } = [];
    public IReadOnlyList<string> TemplateFilePaths { get; init; } = [];
    public IReadOnlyList<string> ToolAssemblyPaths { get; init; } = [];
    public string? MappingConfigPath { get; init; }
}

public sealed class InputFingerprintSnapshot
{
    public const string FileName = "cs2j-input-fingerprints.json";

    public required string SourceName { get; init; }
    public required string SourcePath { get; init; }
    public required string OptionsHash { get; init; }
    public required IReadOnlyList<InputFingerprintEntry> Entries { get; init; }

    public bool Matches(InputFingerprintSnapshot? other)
    {
        if (other == null)
        {
            return false;
        }

        if (!string.Equals(SourcePath, other.SourcePath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(OptionsHash, other.OptionsHash, StringComparison.Ordinal)
            || Entries.Count != other.Entries.Count)
        {
            return false;
        }

        for (var index = 0; index < Entries.Count; index++)
        {
            var current = Entries[index];
            var previous = other.Entries[index];
            if (!string.Equals(current.Path, previous.Path, StringComparison.OrdinalIgnoreCase)
                || current.Kind != previous.Kind
                || !string.Equals(current.ContentHash, previous.ContentHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

public static class InputFingerprintSnapshotBuilder
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static InputFingerprintSnapshot Build(InputFingerprintBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);

        var entries = new List<InputFingerprintEntry>();

        foreach (var inputFilePath in request.InputFilePaths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(CreateEntry(inputFilePath, InputFingerprintEntryKind.SourceInput));
        }

        if (!string.IsNullOrWhiteSpace(request.MappingConfigPath) && File.Exists(request.MappingConfigPath))
        {
            entries.Add(CreateEntry(Path.GetFullPath(request.MappingConfigPath), InputFingerprintEntryKind.MappingConfiguration));
        }

        foreach (var templateFilePath in request.TemplateFilePaths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(CreateEntry(templateFilePath, InputFingerprintEntryKind.TemplateAsset));
        }

        foreach (var toolAssemblyPath in request.ToolAssemblyPaths
                     .Where(File.Exists)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(CreateEntry(toolAssemblyPath, InputFingerprintEntryKind.ToolAssembly));
        }

        return new InputFingerprintSnapshot
        {
            SourceName = request.SourceName,
            SourcePath = Path.GetFullPath(request.SourcePath),
            OptionsHash = ComputeHash(BuildOptionFingerprint(request.OptionTokens)),
            Entries = entries,
        };
    }

    private static InputFingerprintEntry CreateEntry(string path, InputFingerprintEntryKind kind)
    {
        return new InputFingerprintEntry
        {
            Path = path,
            Kind = kind,
            ContentHash = ComputeHash(path),
        };
    }

    private static byte[] BuildOptionFingerprint(IReadOnlyList<string> optionTokens)
    {
        var orderedTokens = optionTokens
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToArray();

        return Utf8WithoutBom.GetBytes(string.Join("\n", orderedTokens));
    }

    private static string ComputeHash(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(sha256.ComputeHash(stream));
    }

    private static string ComputeHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(bytes));
    }
}

public sealed class InputFingerprintSnapshotJsonSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public string Serialize(InputFingerprintSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    public InputFingerprintSnapshot? Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<InputFingerprintSnapshot>(json, SerializerOptions);
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