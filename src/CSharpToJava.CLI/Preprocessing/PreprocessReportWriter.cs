using System.Text;
using System.Text.Json;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// Writes <see cref="PreprocessReport"/> as camelCase, indented, UTF-8 without
/// BOM JSON. Pass order is the chain execution order, so the output stays
/// deterministic for the same inputs/options. Timing values are the only
/// run-dependent fields.
/// </summary>
internal static class PreprocessReportWriter
{
    public const string DefaultFileName = "preprocess-report.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static string DefaultPath(string destinationRoot)
        => Path.Combine(destinationRoot, DefaultFileName);

    public static void Write(string path, PreprocessReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(report);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(report, SerializerOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Rounds a duration to milliseconds for stable, readable JSON.</summary>
    public static double RoundSeconds(TimeSpan elapsed)
        => Math.Round(elapsed.TotalSeconds, 3);

    private static JsonSerializerOptions CreateOptions()
        => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
}
