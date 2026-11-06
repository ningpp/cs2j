using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.ClosedMetadata.ConsumerSurfaces;

/// <summary>
/// Configurable closed-type consumer surfaces (upgrade plan §6.4-R3, W-05/G-04d).
/// The Core implements only symbol-to-entry matching; every library/type name
/// lives in <c>config/ClosedTypeConsumerSurfaces.json</c>. The aggregate also
/// carries the source-generation context base types and attribute names that
/// were historically hard-coded next to the serializer entry family.
/// </summary>
public sealed class ClosedTypeConsumerSurfaces
{
    /// <summary>Canonical configuration file name.</summary>
    public const string DefaultConfigFileName = "ClosedTypeConsumerSurfaces.json";

    /// <summary>Directory below the working/output root that holds the configuration.</summary>
    public const string DefaultConfigSubDirectory = "config";

    public ClosedTypeConsumerSurfaces(
        IClosedTypeConsumerProfile systemTextJson,
        IClosedTypeConsumerProfile yaml,
        IClosedTypeConsumerProfile genericReflection,
        IReadOnlyList<string> contextBaseTypes,
        IReadOnlyList<string> sourceGenAttributes)
    {
        SystemTextJson = systemTextJson;
        Yaml = yaml;
        GenericReflection = genericReflection;
        ContextBaseTypes = contextBaseTypes;
        SourceGenAttributes = sourceGenAttributes;
    }

    /// <summary>STJ serializer/options entry family (CM-ENUM-3, CM-LINK).</summary>
    public IClosedTypeConsumerProfile SystemTextJson { get; }

    /// <summary>YAML serializer consumer surfaces (CM-ENUM-5/6, CM-LINK-2; data-only for now).</summary>
    public IClosedTypeConsumerProfile Yaml { get; }

    /// <summary>Conservative reflection API whitelist (CM-ENUM-5/6; data-only for now).</summary>
    public IClosedTypeConsumerProfile GenericReflection { get; }

    /// <summary>Full names of source-generation context base types (CM-SRCGEN).</summary>
    public IReadOnlyList<string> ContextBaseTypes { get; }

    /// <summary>Simple attribute names of the AOT source-generation declaration (CM-SRCGEN).</summary>
    public IReadOnlyList<string> SourceGenAttributes { get; }

    /// <summary>
    /// True when <paramref name="type"/> derives at any depth from a configured
    /// source-generation context base type (the type itself is not considered,
    /// matching the historical base-chain check).
    /// </summary>
    public bool IsContextBaseType(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            var name = current.ToDisplayString();
            foreach (var configured in ContextBaseTypes)
            {
                if (string.Equals(configured, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="name"/> denotes a configured source-generation
    /// declaration attribute, accepting the short, "Attribute"-suffixed and
    /// qualified spellings (historical name matching).
    /// </summary>
    public bool IsSourceGenAttributeName(string name)
    {
        foreach (var configured in SourceGenAttributes)
        {
            if (string.Equals(name, configured, StringComparison.Ordinal)
                || string.Equals(name, configured + "Attribute", StringComparison.Ordinal)
                || name.EndsWith("." + configured, StringComparison.Ordinal)
                || name.EndsWith("." + configured + "Attribute", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Loads the configuration file. A missing, unreadable or malformed file raises
    /// <see cref="ClosedTypeConsumerSurfaceConfigurationException"/> (never a silent
    /// empty profile).
    /// </summary>
    public static ClosedTypeConsumerSurfaces Load(string path)
    {
        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(Directory.GetCurrentDirectory(), path);

        string text;
        try
        {
            text = File.ReadAllText(resolved);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ClosedTypeConsumerSurfaceConfigurationException(
                $"failed to read the closed-type consumer surface configuration '{resolved}': {exception.Message}",
                exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException exception)
        {
            throw new ClosedTypeConsumerSurfaceConfigurationException(
                $"invalid JSON in the closed-type consumer surface configuration '{resolved}': {exception.Message}",
                exception);
        }

        using (document)
        {
            return FromJson(document.RootElement, resolved);
        }
    }

    /// <summary>
    /// Resolves the configured path; when <paramref name="path"/> is null the
    /// canonical <c>config/ClosedTypeConsumerSurfaces.json</c> is probed below the
    /// application base directory and then the current directory.
    /// </summary>
    public static ClosedTypeConsumerSurfaces LoadDefault(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            return Load(path);
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, DefaultConfigSubDirectory, DefaultConfigFileName),
            Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigSubDirectory, DefaultConfigFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Load(candidate);
            }
        }

        throw new ClosedTypeConsumerSurfaceConfigurationException(
            "closed-type consumer surface configuration not found; looked in: " +
            string.Join(", ", candidates) + ".");
    }

    private static ClosedTypeConsumerSurfaces FromJson(JsonElement root, string path)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Error(path, "the root must be a JSON object.");
        }

        if (!root.TryGetProperty("profiles", out var profiles)
            || profiles.ValueKind != JsonValueKind.Object)
        {
            throw Error(path, "missing object property 'profiles'.");
        }

        return new ClosedTypeConsumerSurfaces(
            ReadProfile(profiles, "systemTextJson", path),
            ReadProfile(profiles, "yaml", path),
            ReadProfile(profiles, "genericReflection", path),
            ReadStringArray(root, "contextBaseTypes", path),
            ReadStringArray(root, "sourceGenAttributes", path));
    }

    private static ClosedTypeConsumerProfile ReadProfile(
        JsonElement profiles,
        string name,
        string path)
    {
        if (!profiles.TryGetProperty(name, out var profile)
            || profile.ValueKind != JsonValueKind.Object)
        {
            throw Error(path, $"missing object property 'profiles.{name}'.");
        }

        if (!profile.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            throw Error(path, $"missing array property 'profiles.{name}.entries'.");
        }

        var parsed = new List<ClosedTypeConsumerSurfaceEntry>();
        foreach (var element in entries.EnumerateArray())
        {
            parsed.Add(ReadEntry(element, profile: name, path));
        }

        return new ClosedTypeConsumerProfile(name, parsed);
    }

    private static ClosedTypeConsumerSurfaceEntry ReadEntry(
        JsonElement element,
        string profile,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error(path, $"every 'profiles.{profile}.entries' item must be a JSON object.");
        }

        var type = ReadRequiredString(element, "type", profile, path);
        return new ClosedTypeConsumerSurfaceEntry(
            ReadOptionalString(element, "assembly") ?? string.Empty,
            type,
            ReadStringArray(element, "methods", path),
            ReadEnum(element, "genericArgPolicy", ClosedGenericArgPolicy.None, profile, path),
            ReadOptionalBool(element, "requiresGuard") ?? false,
            ReadEnum(element, "executionKind", ClosedExecutionKind.Immediate, profile, path));
    }

    private static string ReadRequiredString(
        JsonElement element,
        string name,
        string profile,
        string path)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Error(path, $"property 'profiles.{profile}.entries[].{name}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string? ReadOptionalString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? ReadOptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static TEnum ReadEnum<TEnum>(
        JsonElement element,
        string name,
        TEnum fallback,
        string profile,
        string path)
        where TEnum : struct, Enum
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return fallback;
        }

        var text = value.GetString();
        if (!Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed))
        {
            throw Error(path, $"property 'profiles.{profile}.entries[].{name}' has unknown value '{text}'.");
        }

        return parsed;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string name,
        string path)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw Error(path, $"property '{name}' must be a JSON array of strings.");
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw Error(path, $"property '{name}' must contain strings only.");
            }

            items.Add(item.GetString()!);
        }

        return items;
    }

    private static ClosedTypeConsumerSurfaceConfigurationException Error(string path, string detail)
        => new($"invalid closed-type consumer surface configuration '{path}': {detail}");
}

/// <summary>Raised when the consumer-surface configuration is missing or malformed.</summary>
public sealed class ClosedTypeConsumerSurfaceConfigurationException : Exception
{
    public ClosedTypeConsumerSurfaceConfigurationException(string message)
        : base(message)
    {
    }

    public ClosedTypeConsumerSurfaceConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
