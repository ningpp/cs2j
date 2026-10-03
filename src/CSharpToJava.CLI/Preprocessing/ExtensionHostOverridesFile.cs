using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.ExtensionMethodResolve;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Loads the M4 extension-methods host overrides JSON (design §7):
/// <code>
/// {
///   "hosts": {
///     "Some.Full.ExtensionHost": "skip",
///     "Other.ExtensionHost": "force"
///   },
///   "additionalLinqHosts": ["My.Custom.LinqishHost"]
/// }
/// </code>
/// Action strings are case-insensitive (skip/force). The two protected LINQ
/// hosts are rejected by <see cref="ExtensionHostOverrides"/> validation.
/// </summary>
internal static class ExtensionHostOverridesFile
{
    public static ExtensionHostOverrides? Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Extension-methods overrides file was not found: '{path}'.", path);
        }

        ExtensionHostOverridesFileContent? content;
        try
        {
            content = JsonSerializer.Deserialize<ExtensionHostOverridesFileContent>(
                File.ReadAllText(path), new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Extension-methods overrides file '{path}' is not valid JSON: {ex.Message}", ex);
        }

        if (content is null)
        {
            throw new InvalidDataException($"Extension-methods overrides file '{path}' is empty.");
        }

        var hostActions = new Dictionary<string, ExtensionHostOverrideAction>(StringComparer.Ordinal);
        if (content.Hosts is not null)
        {
            foreach (var (host, actionText) in content.Hosts)
            {
                var action = actionText?.Trim().ToLowerInvariant() switch
                {
                    "skip" => ExtensionHostOverrideAction.Skip,
                    "force" => ExtensionHostOverrideAction.Force,
                    _ => throw new InvalidDataException(
                        $"Extension-methods overrides file '{path}': unknown action '{actionText}' for host "
                        + $"'{host}' (expected 'skip' or 'force')."),
                };
                hostActions[host] = action;
            }
        }

        var additionalLinqHosts = new HashSet<string>(
            content.AdditionalLinqHosts ?? Array.Empty<string>(), StringComparer.Ordinal);

        return new ExtensionHostOverrides(hostActions, additionalLinqHosts);
    }

    private sealed class ExtensionHostOverridesFileContent
    {
        [JsonPropertyName("hosts")]
        public Dictionary<string, string>? Hosts { get; set; }

        [JsonPropertyName("additionalLinqHosts")]
        public IReadOnlyList<string>? AdditionalLinqHosts { get; set; }
    }
}
