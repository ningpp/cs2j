// Compact JSON + markdown rendering for one profile run. The full per-project
// metrics and capped diagnostic samples are included; raw CLI logs (if any)
// stay only in the gitignored --out/cli directory.

using System.Text;
using System.Text.Json;

internal static class ProbeReport
{
    public static IReadOnlyList<ProbeProfile> AllProfiles() => new ProbeProfile[]
    {
        new ExpressionDesugarProfile(interpolation: true),
        new ExpressionDesugarProfile(interpolation: false),
        new NumericBoundaryProfile(),
        new LexicalNormalizeProfile(literals: false, fileScoped: false),
        new LexicalNormalizeProfile(literals: false, fileScoped: true),
        new LexicalNormalizeProfile(literals: true, fileScoped: false),
        new ForeachSourceProfile(),
        new MemberDesugarInProcProfile(),
        new MemberDesugarCliProfile(),
    };

    public static string Headline(ProbeRun run)
    {
        var files = run.Projects.Sum(p => p.Files);
        var diagTotal = run.Diagnostics.Sum(d => d.Count);
        return $"[probe] {run.ProfileId}: {run.Projects.Count} project(s) " +
               $"({run.ProjectsUnavailable} unavailable), {files} file(s), " +
               $"{run.Totals.Count} metric bucket(s), {diagTotal} diagnostic(s)";
    }

    public static string BuildJson(ProbeRun run)
    {
        return JsonSerializer.Serialize(new
        {
            generatedAt = run.GeneratedAt,
            profile = run.ProfileId,
            family = run.Family,
            description = run.Description,
            corpus = run.Corpus,
            root = run.Root,
            note = run.Note,
            projectsTotal = run.Projects.Count,
            projectsUnavailable = run.ProjectsUnavailable,
            filesTotal = run.Projects.Sum(p => p.Files),
            totals = run.Totals,
            diagnostics = run.Diagnostics
                .OrderByDescending(d => d.Count)
                .Select(d => new
                {
                    code = d.Code,
                    severity = d.Severity,
                    count = d.Count,
                    byUnit = d.ByUnit,
                    samples = d.Samples,
                }),
            projects = run.Projects.Select(p => new
            {
                unit = p.Unit,
                files = p.Files,
                transformed = p.FilesTransformed,
                filesWithErrors = p.FilesWithErrors,
                compileErrors = p.CompileErrors,
                missingCompileItems = p.MissingCompileItems,
                driverErrors = p.DriverErrors,
                unavailable = p.UnavailableReason,
                note = p.Note,
                errorCodesTop = p.ErrorCodes.OrderByDescending(kv => kv.Value).Take(6),
                metrics = p.Metrics.Where(kv => kv.Value != 0)
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal),
            }),
            rawSummaryLines = run.RawSummaryLines,
        }, new JsonSerializerOptions { WriteIndented = false });
    }

    public static string BuildMarkdown(ProbeRun run)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {run.ProfileId}（{run.Family}）语料画像 — {run.Corpus}");
        sb.AppendLine();
        sb.AppendLine($"- 生成时间：{run.GeneratedAt}");
        sb.AppendLine($"- 语料根：`{run.Root}`");
        sb.AppendLine($"- 画像：{run.Description}");
        if (!string.IsNullOrEmpty(run.Note))
            sb.AppendLine($"- 口径备注：{run.Note}");
        sb.AppendLine($"- 项目：{run.Projects.Count}（不可用 {run.ProjectsUnavailable}）；" +
                      $"文件：{run.Projects.Sum(p => p.Files)}（改写 {run.Projects.Sum(p => p.FilesTransformed)}）");
        sb.AppendLine();

        sb.AppendLine("## 聚合计数");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 计数 |");
        sb.AppendLine("|---|---:|");
        foreach (var (key, value) in run.Totals.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"| {key} | {value} |");
        sb.AppendLine();

        sb.AppendLine("## 诊断码（命中/SkippedByReason）");
        sb.AppendLine();
        if (run.Diagnostics.Count == 0)
        {
            sb.AppendLine("（无诊断）");
        }
        else
        {
            sb.AppendLine("| 码 | 级别 | 计数 | 分布（unit：计数，前 6） | 样例 |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var d in run.Diagnostics.OrderByDescending(d => d.Count))
            {
                var distribution = string.Join("，",
                    d.ByUnit.OrderByDescending(kv => kv.Value).Take(6)
                        .Select(kv => $"{kv.Key}:{kv.Value}"));
                var sample = d.Samples.FirstOrDefault();
                var sampleText = sample is null
                    ? ""
                    : $"`{Escape(sample.Location)}` {Escape(sample.Message)}";
                sb.AppendLine($"| {d.Code} | {d.Severity} | {d.Count} | {Escape(distribution)} | {sampleText} |");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 每项目（仅列非零指标）");
        sb.AppendLine();
        sb.AppendLine("| 项目 | 文件 | 改写 | 编译错误 | 指标 |");
        sb.AppendLine("|---|---:|---:|---:|---|");
        foreach (var p in run.Projects)
        {
            var metricText = p.UnavailableReason is not null
                ? $"**不可用**：{Escape(p.NoteOrReason())}"
                : Escape(string.Join("，",
                    p.Metrics.Where(kv => kv.Value != 0)
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}={kv.Value}")));
            sb.AppendLine($"| {p.Unit} | {p.Files} | {p.FilesTransformed} | {p.CompileErrors} | {metricText} |");
        }
        sb.AppendLine();

        if (run.RawSummaryLines.Count > 0)
        {
            sb.AppendLine("## 适配器原始摘要行");
            sb.AppendLine();
            foreach (var line in run.RawSummaryLines)
                sb.AppendLine($"- `{Escape(line)}`");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Escape(string text)
        => text.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");
}

internal static class ProjectRowExtensions
{
    public static string NoteOrReason(this ProjectRow row) =>
        row.UnavailableReason ?? row.Note ?? "";
}
