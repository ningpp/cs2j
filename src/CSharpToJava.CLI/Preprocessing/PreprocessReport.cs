using System.Text.Json.Serialization;

namespace CSharpToJava.CLI.Preprocessing;

/// <summary>
/// M0: per-run preprocess chain report written to
/// <c>&lt;destination&gt;/preprocess-report.json</c>. Records every executed pass
/// in deterministic chain order, its timing/stage breakdown, summary and
/// diagnostics, so the pass-level observability no longer lives only on the
/// console. <see cref="SchemaVersion"/> is fixed at 1.
/// </summary>
internal sealed record PreprocessReport(
    int SchemaVersion,
    string Profile,
    string Source,
    string Destination,
    bool Success,
    double TotalSeconds,
    IReadOnlyList<PassReportEntry> Passes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Manifest = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SubsetStatus = null);

/// <summary>
/// One executed pass. <see cref="Status"/> is <c>ok</c> or <c>failed</c>;
/// passes after a failure are not executed and therefore not reported.
/// </summary>
internal sealed record PassReportEntry(
    string Id,
    string Status,
    double TotalSeconds,
    IReadOnlyDictionary<string, double> Stages,
    string Summary,
    IReadOnlyList<string> Diagnostics,
    string? IntermediateDirectory);
