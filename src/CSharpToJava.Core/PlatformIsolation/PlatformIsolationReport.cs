using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharpToJava.Core.Context;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// Cs2jPlatformIsolation.json 配置模型（设计 §4.4）。<see cref="Policies"/> 按
/// family 覆盖默认档；<see cref="Allowlist"/> 追加 compat 前缀/身份前缀；
/// <see cref="Suppress"/> 显式知情残留；<see cref="ProbeDefaults"/> 探针保守值覆盖。
/// </summary>
public sealed class PlatformIsolationConfig
{
    public Dictionary<string, PlatformIsolationDefaultAction>? Policies { get; set; }

    public List<string>? Allowlist { get; set; }

    public List<PlatformIsolationSuppressEntry>? Suppress { get; set; }

    public Dictionary<string, string>? ProbeDefaults { get; set; }
}

/// <summary>suppress 条目（设计 §4.4 / §4.6：键含 file+span+family+理由+owner）。</summary>
public sealed class PlatformIsolationSuppressEntry
{
    public string? File { get; set; }

    public int[]? Span { get; set; }

    public string? Family { get; set; }

    public string? Reason { get; set; }

    public string? Owner { get; set; }
}

/// <summary>
/// 源根配置加载（设计 §4.4 / D4）：显式 ConfigPath 或 CWD 向上查找
/// <c>Cs2jPlatformIsolation.json</c>（沿用 C25 manifest/overrides 惯例）。
/// </summary>
public static class PlatformIsolationConfigLoader
{
    public const string FileName = "Cs2jPlatformIsolation.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public static PlatformIsolationConfig? Load(string? configPath, string? startDirectory = null)
    {
        var resolved = ResolvePath(configPath, startDirectory);
        if (resolved == null)
        {
            return null;
        }

        var json = File.ReadAllText(resolved);
        return JsonSerializer.Deserialize<PlatformIsolationConfig>(json, SerializerOptions);
    }

    private static string? ResolvePath(string? configPath, string? startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var path = Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(Directory.GetCurrentDirectory(), configPath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Platform isolation configuration file not found: {path}", path);
            }

            return path;
        }

        var directory = new DirectoryInfo(startDirectory ?? Directory.GetCurrentDirectory());
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>报告 JSON 统计字段（设计 §4.6）。</summary>
public sealed class PlatformIsolationTotals
{
    public required int Sites { get; init; }
    public required int Reject { get; init; }
    public required int Stub { get; init; }
    public required int Facade { get; init; }
    public required int Suppressed { get; init; }
    public required int Residual { get; init; }
}

/// <summary>报告 JSON 站点条目（设计 §4.6）。</summary>
public sealed class PlatformIsolationSiteEntry
{
    public required string File { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int[] Span { get; init; }
    public required string Family { get; init; }
    public required string Code { get; init; }
    public required string Identity { get; init; }
    public required string SyntaxKind { get; init; }
    public required string DefaultAction { get; init; }
    public required string Action { get; init; }
    public string? Replacement { get; init; }
    public required string Diagnostic { get; init; }
    public string? ConfigMatch { get; init; }
    public required string Confidence { get; init; }
}

/// <summary>报告 JSON 模型（字节稳定契约见 <see cref="PlatformIsolationReportSerializer"/>）。</summary>
public sealed class PlatformIsolationReport
{
    public string Tool { get; init; } = "cs2j platform-isolation";
    public required string Mode { get; init; }
    public required string GeneratedAtUtc { get; init; }
    public required PlatformIsolationTotals Totals { get; init; }
    public required IReadOnlyList<PlatformIsolationSiteEntry> Sites { get; init; }
}

/// <summary>
/// 报告 JSON 序列化（设计 §4.6）：站点按 (file, line, column, code) 排序；
/// UTF-8 无 BOM；camelCase 属性与枚举。
/// </summary>
public sealed class PlatformIsolationReportSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public string Serialize(PlatformIsolationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    /// <summary>写盘为 UTF-8 无 BOM（字节稳定契约）。</summary>
    public void WriteToFile(string path, PlatformIsolationReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, Serialize(report), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>报告构建产物：报告 + PI0xx 诊断 + 统计。</summary>
public sealed class PlatformIsolationReportBuildResult
{
    public required PlatformIsolationReport Report { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
    public required PlatformIsolationStatistics Statistics { get; init; }
}

/// <summary>
/// 报告构建器（设计 §4.6 / §6）：应用配置 policy 覆盖与 suppress 匹配，
/// 发射 PI001（每站点）、PI060（配置 suppress）、PI061（陈旧条目）、
/// PI070（写出对账行）、PI090（身份降级）。
/// </summary>
public static class PlatformIsolationReportBuilder
{
    public static PlatformIsolationReportBuildResult Build(
        BoundaryInventory inventory,
        PlatformIsolationOptions options,
        PlatformIsolationConfig? config)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);

        var diagnostics = new List<PlatformIsolationDiagnostic>();
        var entries = new List<PlatformIsolationSiteEntry>();
        var resolvedActions = new List<PlatformIsolationAction>();
        var matchedSuppressKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var site in inventory.Sites)
        {
            var policyOverride = ResolvePolicy(config, site.Family);
            var suppressEntry = FindSuppressEntry(config, site);
            var action = ResolveAction(site, policyOverride, suppressEntry != null);
            var configMatch = suppressEntry != null ? SuppressKey(suppressEntry) : null;
            if (configMatch != null)
            {
                matchedSuppressKeys.Add(configMatch);
                diagnostics.Add(BuildSuppressedDiagnostic(site, suppressEntry!, options));
            }

            entries.Add(new PlatformIsolationSiteEntry
            {
                File = site.File,
                Line = site.Line,
                Column = site.Column,
                Span = new[] { site.SpanStart, site.SpanEnd },
                Family = ToCamelCase(site.Family.ToString()),
                Code = site.Code,
                Identity = site.Identity,
                SyntaxKind = site.SyntaxKind,
                DefaultAction = ToCamelCase(site.DefaultAction.ToString()),
                Action = ToCamelCase(action.ToString()),
                Replacement = null,
                Diagnostic = PlatformIsolationDiagnosticCodes.SiteRecorded,
                ConfigMatch = configMatch,
                Confidence = site.Confidence.ToString(),
            });

            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.SiteRecorded,
                DiagnosticSeverity.Info,
                $"{site.Family} site '{site.Identity}' at {site.File}:{site.Line}:{site.Column}: {action}; " +
                "recorded by platform-isolation report mode",
                site.File));

            if (site.Confidence == BoundaryConfidence.SyntaxFallback)
            {
                diagnostics.Add(new PlatformIsolationDiagnostic(
                    PlatformIsolationDiagnosticCodes.CrossFilePlanSkipped,
                    DiagnosticSeverity.Warning,
                    $"Platform boundary site '{site.Identity}' at {site.File}:{site.Line}:{site.Column} " +
                    "resolved without a semantic model; identity confidence downgraded to SyntaxFallback.",
                    site.File));
            }

            resolvedActions.Add(action);
        }

        if (config?.Suppress != null)
        {
            foreach (var entry in config.Suppress)
            {
                if (!matchedSuppressKeys.Contains(SuppressKey(entry)))
                {
                    diagnostics.Add(new PlatformIsolationDiagnostic(
                        PlatformIsolationDiagnosticCodes.StaleConfigEntry,
                        DiagnosticSeverity.Warning,
                        $"Stale platform-isolation config entry (no matching site): {SuppressKey(entry)}.",
                        entry.File));
                }
            }
        }

        var statistics = PlatformIsolationStatistics.FromResolved(inventory.Sites, resolvedActions);
        var report = new PlatformIsolationReport
        {
            Mode = options.Mode.ToString(),
            GeneratedAtUtc = DateTime.UtcNow.ToString("O"),
            Totals = new PlatformIsolationTotals
            {
                Sites = statistics.TotalSites,
                Reject = statistics.Rejected,
                Stub = statistics.Stubbed,
                Facade = statistics.Facaded,
                Suppressed = statistics.Suppressed,
                Residual = statistics.Residual,
            },
            Sites = entries,
        };

        diagnostics.Add(new PlatformIsolationDiagnostic(
            PlatformIsolationDiagnosticCodes.ReportWritten,
            DiagnosticSeverity.Info,
            $"platform-isolation report: {statistics.TotalSites} site(s); " +
            $"reject={statistics.Rejected}, stub={statistics.Stubbed}, facade={statistics.Facaded}, " +
            $"suppressed={statistics.Suppressed}, residual={statistics.Residual}; " +
            $"reconciled={statistics.InvariantHolds}"));

        return new PlatformIsolationReportBuildResult
        {
            Report = report,
            Diagnostics = diagnostics,
            Statistics = statistics,
        };
    }

    private static PlatformIsolationAction ResolveAction(
        BoundarySite site,
        PlatformIsolationDefaultAction? policyOverride,
        bool suppressedByConfig)
    {
        if (suppressedByConfig)
        {
            return PlatformIsolationAction.Suppressed;
        }

        return PlatformIsolationActionResolver.Resolve(site, policyOverride);
    }

    private static PlatformIsolationDefaultAction? ResolvePolicy(PlatformIsolationConfig? config, BoundaryFamily family)
    {
        if (config?.Policies == null)
        {
            return null;
        }

        return config.Policies.TryGetValue(family.ToString(), out var action)
               || config.Policies.TryGetValue(ToCamelCase(family.ToString()), out action)
            ? action
            : null;
    }

    private static PlatformIsolationSuppressEntry? FindSuppressEntry(PlatformIsolationConfig? config, BoundarySite site)
    {
        if (config?.Suppress == null)
        {
            return null;
        }

        foreach (var entry in config.Suppress)
        {
            if (Matches(entry, site))
            {
                return entry;
            }
        }

        return null;
    }

    private static bool Matches(PlatformIsolationSuppressEntry entry, BoundarySite site)
    {
        if (!string.IsNullOrEmpty(entry.File) && !string.Equals(entry.File, site.File, StringComparison.Ordinal))
        {
            return false;
        }

        if (entry.Span is { Length: 2 }
            && (entry.Span[0] != site.SpanStart || entry.Span[1] != site.SpanEnd))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(entry.Family)
            && !string.Equals(entry.Family, site.Family.ToString(), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entry.Family, ToCamelCase(site.Family.ToString()), StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static PlatformIsolationDiagnostic BuildSuppressedDiagnostic(
        BoundarySite site,
        PlatformIsolationSuppressEntry entry,
        PlatformIsolationOptions options)
    {
        var severity = options.Strict ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
        var reason = string.IsNullOrWhiteSpace(entry.Reason) ? "no reason given" : entry.Reason;
        var owner = string.IsNullOrWhiteSpace(entry.Owner) ? "unowned" : entry.Owner;
        return new PlatformIsolationDiagnostic(
            PlatformIsolationDiagnosticCodes.ConfigSuppressed,
            severity,
            $"Platform boundary site '{site.Identity}' at {site.File}:{site.Line}:{site.Column} " +
            $"suppressed by config (reason: {reason}; owner: {owner}).",
            site.File);
    }

    private static string SuppressKey(PlatformIsolationSuppressEntry entry)
    {
        var span = entry.Span is { Length: 2 } ? $"{entry.Span[0]}-{entry.Span[1]}" : "*";
        return $"{entry.File ?? "*"}|{span}|{entry.Family ?? "*"}";
    }

    internal static string ToCamelCase(string value)
        => string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}