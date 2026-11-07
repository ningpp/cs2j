using System.Text.Json;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>Outcome of validating an existing tree's manifest.</summary>
internal sealed record ManifestValidationResult(
    bool IsValid,
    string? Message,
    string SubsetStatus,
    string? ManifestPath);

/// <summary>
/// R12: <c>--no-preprocess</c> handshake. The tree is re-scanned from disk
/// (solution variant + reference set + source contents) so the manifest can
/// never vouch for itself. java-ready/audit fail with
/// <see cref="PreprocessDiagnosticCodes.ManifestHandshakeFailed"/>; other
/// profiles continue and report <c>unverified</c>.
/// </summary>
internal static class PreprocessManifestValidator
{
    public static ManifestValidationResult ValidateForExistingTree(
        string inputPath,
        string profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);

        var required = JavaConversionRequirements.RequiredCapabilitiesFor(profile);
        var requiresManifest = JavaConversionRequirements.RequiresManifest(profile);
        var manifestPath = PreprocessManifestWriter.FindFromInput(inputPath);

        if (manifestPath is null)
        {
            var message =
                "no preprocess-manifest.json was found from the input path (searched up to 8 levels). " +
                "请先运行 preprocess / --preprocess-only 生成带 manifest 的最终树。";
            return requiresManifest
                ? Invalid(message, null)
                : new ManifestValidationResult(
                    IsValid: true,
                    Message: "[Warning] CS2J1410: " + message,
                    SubsetStatus: "unverified",
                    ManifestPath: null);
        }

        var treeRoot = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrEmpty(treeRoot))
        {
            return Invalid($"manifest path has no parent directory: {manifestPath}", manifestPath);
        }

        string json;
        string? readError;
        if (!PreprocessManifestWriter.TryRead(treeRoot, out json, out readError))
        {
            return Invalid(readError ?? "failed to read preprocess manifest.", manifestPath);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Invalid($"preprocess manifest is not valid JSON: {ex.Message}", manifestPath);
        }

        using (document)
        {
            var root = document.RootElement;

            var schemaVersion = root.TryGetProperty("schemaVersion", out var schema)
                ? schema.GetInt32()
                : 0;
            if (schemaVersion != PreprocessManifest.ManifestSchemaVersion)
            {
                return MaybeFail(
                    $"manifest schemaVersion {schemaVersion} does not match " +
                    $"{PreprocessManifest.ManifestSchemaVersion}.",
                    requiresManifest,
                    manifestPath);
            }

            var subsetVersion = root.TryGetProperty("subsetVersion", out var subset)
                ? subset.GetString()
                : null;
            if (!string.Equals(
                    subsetVersion,
                    PreprocessManifest.ManifestSubsetVersion,
                    StringComparison.Ordinal))
            {
                return MaybeFail(
                    $"manifest subsetVersion '{subsetVersion}' does not match " +
                    $"'{PreprocessManifest.ManifestSubsetVersion}'.",
                    requiresManifest,
                    manifestPath);
            }

            if (!root.TryGetProperty("inputHash", out var hashElement)
                || string.IsNullOrEmpty(hashElement.GetString()))
            {
                return MaybeFail("manifest has no inputHash.", requiresManifest, manifestPath);
            }

            var variant = ManifestSolutionVariant.FromTree(treeRoot, "default");
            var actualHash = ManifestInputHash.Compute(treeRoot, variant);
            if (!string.Equals(hashElement.GetString(), actualHash, StringComparison.Ordinal))
            {
                return MaybeFail(
                    $"manifest inputHash does not match the current tree (manifest " +
                    $"{hashElement.GetString()}, tree {actualHash}); the preprocessed tree drifted.",
                    requiresManifest,
                    manifestPath);
            }

            if (required.Count > 0)
            {
                var statuses = ReadCapabilityStatuses(root);
                var missing = required
                    .Where(id => !string.Equals(
                        GetStatus(statuses, id),
                        "guaranteed",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(JcsCapabilityIds.Format)
                    .ToList();

                if (missing.Count > 0)
                {
                    return MaybeFail(
                        "required capabilities are not guaranteed: " + string.Join(", ", missing) + ".",
                        requiresManifest,
                        manifestPath);
                }
            }
        }

        return new ManifestValidationResult(
            IsValid: true,
            Message: null,
            SubsetStatus: "verified",
            ManifestPath: manifestPath);
    }

    private static Dictionary<string, string> ReadCapabilityStatuses(JsonElement root)
    {
        var statuses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
        {
            return statuses;
        }

        foreach (var capability in capabilities.EnumerateArray())
        {
            if (!capability.TryGetProperty("id", out var id)
                || !capability.TryGetProperty("status", out var status))
            {
                continue;
            }

            var idText = id.GetString();
            var statusText = status.GetString();
            if (!string.IsNullOrEmpty(idText) && !string.IsNullOrEmpty(statusText))
            {
                statuses[idText] = statusText;
            }
        }

        return statuses;
    }

    private static string? GetStatus(
        IReadOnlyDictionary<string, string> statuses,
        JcsCapabilityId id)
        => statuses.TryGetValue(JcsCapabilityIds.Format(id), out var status) ? status : null;

    private static ManifestValidationResult MaybeFail(
        string detail,
        bool requiresManifest,
        string manifestPath)
    {
        if (requiresManifest)
        {
            return Invalid(detail, manifestPath);
        }

        return new ManifestValidationResult(
            IsValid: true,
            Message: "[Warning] CS2J1410: " + detail,
            SubsetStatus: "unverified",
            ManifestPath: manifestPath);
    }

    private static ManifestValidationResult Invalid(string detail, string? manifestPath)
        => new(
            IsValid: false,
            Message:
                $"{PreprocessDiagnosticCodes.ManifestHandshakeFailed}: {detail}\n" +
                "请先运行 preprocess / --preprocess-only 重新生成带 manifest 的最终树。",
            SubsetStatus: "unverified",
            ManifestPath: manifestPath);
}
