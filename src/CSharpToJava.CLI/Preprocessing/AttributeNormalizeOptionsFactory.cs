using System.Text.Json;
using CSharpToJava.Core.AttributeNormalize;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Builds the <see cref="AttributeNormalizeOptions"/> for the C10 pass. The
/// optional overrides JSON (default <c>config/AttributeNormalizeOverrides.json</c>,
/// searched upward from the CWD, then from the executable directory) carries the
/// keep/ignore attribute sets, skip-types set and the caller-file-path policy:
/// a missing file means built-in defaults; a malformed file produces a Warning and
/// the run continues with empty sets (design §3.2/§4.1; BuildJavaKeywordOptions
/// precedent).
/// </summary>
internal static class AttributeNormalizeOptionsFactory
{
    public static AttributeNormalizeOptions Build(
        string? explicitPath,
        bool strict,
        bool reportInfo)
    {
        var path = ResolvePath(explicitPath);

        var keepAttributes = new HashSet<string>(StringComparer.Ordinal);
        var ignoreAttributes = new HashSet<string>(StringComparer.Ordinal);
        var skipTypes = new HashSet<string>(StringComparer.Ordinal);
        var callerFilePathPolicy = CallerFilePathPolicy.FreezeAnalyzed;

        if (path != null && File.Exists(path))
        {
            try
            {
                // CLI 帮助文本承诺 camelCase 键（keepAttributes/ignoreAttributes/
                // skipTypes/callerFilePathPolicy），反序列化必须大小写不敏感。
                var jsonOptions = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                };
                var parsed = JsonSerializer.Deserialize<OverrideSet>(File.ReadAllText(path), jsonOptions)
                    ?? new OverrideSet();
                if (parsed.KeepAttributes != null) keepAttributes.UnionWith(parsed.KeepAttributes);
                if (parsed.IgnoreAttributes != null) ignoreAttributes.UnionWith(parsed.IgnoreAttributes);
                if (parsed.SkipTypes != null) skipTypes.UnionWith(parsed.SkipTypes);
                if (!string.IsNullOrWhiteSpace(parsed.CallerFilePathPolicy))
                {
                    callerFilePathPolicy = Enum.Parse<CallerFilePathPolicy>(
                        parsed.CallerFilePathPolicy!, ignoreCase: true);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[Warning] Failed to load attribute-normalize overrides from {path}: {ex.Message}");
                keepAttributes.Clear();
                ignoreAttributes.Clear();
                skipTypes.Clear();
                callerFilePathPolicy = CallerFilePathPolicy.FreezeAnalyzed;
            }
        }

        return new AttributeNormalizeOptions
        {
            Strict = strict,
            ReportInfo = reportInfo,
            CallerFilePathPolicy = callerFilePathPolicy,
            KeepAttributes = keepAttributes,
            IgnoreAttributes = ignoreAttributes,
            SkipTypes = skipTypes,
        };
    }

    private static string? ResolvePath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        var cwdPath = Path.Combine(
            Directory.GetCurrentDirectory(), "config", "AttributeNormalizeOverrides.json");
        if (File.Exists(cwdPath))
        {
            return cwdPath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "AttributeNormalizeOverrides.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private sealed class OverrideSet
    {
        public string[]? KeepAttributes { get; set; }
        public string[]? IgnoreAttributes { get; set; }
        public string[]? SkipTypes { get; set; }
        public string? CallerFilePathPolicy { get; set; }
    }
}
