using System.Text;
using System.Text.Json;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// M0-b：normalization-audit 只读 pass 的残留报告模型。SchemaVersion 固定为 1。
/// </summary>
public sealed record ResidualReport(
    int SchemaVersion,
    string Profile,
    IReadOnlyList<string> Chain,
    ResidualReportSummary Summary,
    IReadOnlyList<ResidualReportEntry> Residuals);

public sealed record ResidualReportSummary(
    int FilesScanned,
    int Residuals,
    int Errors,
    int Warnings);

public sealed record ResidualReportEntry(
    string Code,
    string File,
    int Line,
    int Column,
    string Snippet,
    string OwnerPassId,
    string Policy,
    string Reason);

/// <summary>
/// 残留报告 JSON 写入器：camelCase 属性、WriteIndented、UTF-8 无 BOM；
/// 写入前按 (File, Line, Column, Code, Snippet) 排序，保证跨运行确定性。
/// Chain 保持调用方传入的顺序。
/// </summary>
public static class ResidualReportWriter
{
    public const string DefaultFileName = "preprocess-residual-report.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static void Write(string path, ResidualReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(report);

        var sorted = report with
        {
            Residuals = report.Residuals
                .OrderBy(entry => entry.File, StringComparer.Ordinal)
                .ThenBy(entry => entry.Line)
                .ThenBy(entry => entry.Column)
                .ThenBy(entry => entry.Code, StringComparer.Ordinal)
                .ThenBy(entry => entry.Snippet, StringComparer.Ordinal)
                .ToList(),
        };

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(sorted, SerializerOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static JsonSerializerOptions CreateOptions()
        => new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
}
