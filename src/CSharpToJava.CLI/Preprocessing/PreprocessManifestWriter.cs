using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Deterministic JSON surface for <see cref="PreprocessManifest"/>: camelCase,
/// indented, LF line endings, UTF-8 without BOM (the writer uses
/// <c>UTF8Encoding(false)</c>), null members omitted.
/// </summary>
internal static class PreprocessManifestJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public static string Serialize(PreprocessManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, SerializerOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions()
        => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters =
            {
                new JcsCapabilityIdJsonConverter(),
                new JcsCapabilityStatusJsonConverter(),
            },
        };
}

/// <summary>Writes capability ids as the canonical <c>"JCS.&lt;ID&gt;"</c> string.</summary>
internal sealed class JcsCapabilityIdJsonConverter : JsonConverter<JcsCapabilityId>
{
    public override JcsCapabilityId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!JcsCapabilityIds.TryParse(text, out var id))
        {
            throw new JsonException($"Unknown JCS capability id: '{text}'.");
        }

        return id;
    }

    public override void Write(
        Utf8JsonWriter writer,
        JcsCapabilityId value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(JcsCapabilityIds.Format(value));
}

/// <summary>Writes capability status as lower camel case (<c>guaranteed</c>).</summary>
internal sealed class JcsCapabilityStatusJsonConverter : JsonConverter<JcsCapabilityStatus>
{
    public override JcsCapabilityStatus Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return text is not null
            && Enum.TryParse<JcsCapabilityStatus>(text, ignoreCase: true, out var status)
                ? status
                : throw new JsonException($"Unknown JCS capability status: '{text}'.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        JcsCapabilityStatus value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(
            JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
}

/// <summary>
/// R12: writes/reads <c>preprocess-manifest.json</c> at a tree root and finds
/// it by walking up from a <c>--no-preprocess</c> input path (the manifest
/// directory is the tree root).
/// </summary>
internal static class PreprocessManifestWriter
{
    public const string DefaultFileName = "preprocess-manifest.json";

    public static string Write(string treeRoot, PreprocessManifest manifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        ArgumentNullException.ThrowIfNull(manifest);

        var root = Path.GetFullPath(treeRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, DefaultFileName);

        File.WriteAllText(
            path,
            PreprocessManifestJson.Serialize(manifest),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    public static bool TryRead(string treeRoot, out string json, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);

        var path = Path.Combine(Path.GetFullPath(treeRoot), DefaultFileName);
        if (!File.Exists(path))
        {
            json = string.Empty;
            error = $"preprocess manifest not found: {path}";
            return false;
        }

        try
        {
            json = File.ReadAllText(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            json = string.Empty;
            error = $"failed to read preprocess manifest '{path}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Walks up at most <paramref name="maxDepth"/> directories from the input
    /// file/directory looking for <see cref="DefaultFileName"/>.
    /// </summary>
    public static string? FindFromInput(string inputPath, int maxDepth = 8)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);

        var directory = Directory.Exists(inputPath)
            ? Path.GetFullPath(inputPath)
            : Path.GetDirectoryName(Path.GetFullPath(inputPath));

        for (var depth = 0; depth < maxDepth && !string.IsNullOrEmpty(directory); depth++)
        {
            var candidate = Path.Combine(directory, DefaultFileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
