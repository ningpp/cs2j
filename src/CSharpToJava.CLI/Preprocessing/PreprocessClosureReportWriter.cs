using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// R13: deterministic <c>preprocess-closure-report.json</c> written next to the
/// manifest at the final tree root. camelCase, indented, UTF-8 without BOM, LF,
/// tree-relative <c>/</c> paths, no timestamps.
/// </summary>
internal static class PreprocessClosureReportWriter
{
    public const string DefaultFileName = "preprocess-closure-report.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static string Write(string treeRoot, SemanticClosureScanResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(treeRoot);
        ArgumentNullException.ThrowIfNull(result);

        var root = Path.GetFullPath(treeRoot);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, DefaultFileName);

        var report = new PreprocessClosureReport(
            SchemaVersion: 1,
            Constructs: result.Constructs,
            Unowned: result.UnownedConstructs,
            Finalizers: result.Finalizers,
            IsClosed: result.IsClosed,
            Summary: new PreprocessClosureSummary(
                ConstructCount: result.Constructs.Count,
                UnownedCount: result.UnownedConstructs.Count,
                FinalizerCount: result.Finalizers.Count));

        var json = JsonSerializer.Serialize(report, SerializerOptions)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        File.WriteAllText(
            path,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return path;
    }

    private static JsonSerializerOptions CreateOptions()
        => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
}

internal sealed record PreprocessClosureReport(
    int SchemaVersion,
    IReadOnlyList<SemanticClosureConstruct> Constructs,
    IReadOnlyList<SemanticClosureResidual> Unowned,
    IReadOnlyList<SemanticFinalizerExecution> Finalizers,
    bool IsClosed,
    PreprocessClosureSummary Summary);

internal sealed record PreprocessClosureSummary(
    int ConstructCount,
    int UnownedCount,
    int FinalizerCount);
