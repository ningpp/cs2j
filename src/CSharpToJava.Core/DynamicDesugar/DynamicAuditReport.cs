using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>报告 JSON 汇总字段（设计 §4.6；口径取自 <see cref="DynamicDesugarStatistics"/>）。</summary>
public sealed class DynamicAuditTotals
{
    /// <summary>扫描过的源文件数（清单内出现的不同路径数）。</summary>
    public required int FilesScanned { get; init; }

    /// <summary>去重后的站点总数。</summary>
    public required int Sites { get; init; }

    /// <summary>声明族站点数（局部/参数/返回/字段属性事件）。</summary>
    public required int DeclarationsClassified { get; init; }

    /// <summary>P1 已具体化站点数（M2 起非零）。</summary>
    public required int ProvenDeclarations { get; init; }

    /// <summary>带不可证原因的站点数。</summary>
    public required int UnprovableSites { get; init; }

    /// <summary>命中 manifest 的残余站点数（M2 起非零）。</summary>
    public required int ManifestSuppressed { get; init; }

    /// <summary>manifest 陈旧条目数（M2 起非零）。</summary>
    public required int ManifestStaleEntries { get; init; }

    /// <summary>残留站点数（M2 闸门）。</summary>
    public required int ResidualSites { get; init; }

    /// <summary>按 kind 的明细（13 个键恒在、camelCase、枚举顺序稳定）。</summary>
    public required IReadOnlyDictionary<DynamicSiteKind, int> SitesByKind { get; init; }
}

/// <summary>报告 JSON 站点条目（设计 §4.6；行列 1 基）。</summary>
public sealed class DynamicAuditSiteEntry
{
    public required string File { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    /// <summary>字符级 [start, end) 半开区间。</summary>
    public required int[] Span { get; init; }

    public required DynamicSiteKind Kind { get; init; }

    public required string Code { get; init; }

    public required string SyntaxKind { get; init; }

    /// <summary>绑定符号显示名（晚绑定站点为 null）。</summary>
    public string? BoundSymbol { get; init; }

    /// <summary>接收者表达式文本（能取到时）。</summary>
    public string? Receiver { get; init; }

    /// <summary>表达式/签名摘要文本。</summary>
    public string? Expression { get; init; }

    public required DynamicProvability Provability { get; init; }

    /// <summary>不可证原因（M1 通常为 null）。</summary>
    public DynamicUnprovableReason? Reason { get; init; }

    /// <summary>T→dynamic 边界处的来源类型 T 显示名。</summary>
    public string? OriginType { get; init; }

    public required bool ManifestHit { get; init; }

    /// <summary>引用缺失保守识别（R11）。</summary>
    public required bool SymbolUnresolved { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// dynamic-desugar 审计报告模型（设计 §4.6）。字节稳定契约：<see cref="Tool"/> 固定、
/// 禁止时间戳/GUID/机器名；站点按 (file, spanStart, spanEnd, kind, code) 排序（清单
/// 已排序，直接映射），同一输入跨两次运行序列化结果逐字节相同。
/// </summary>
public sealed class DynamicAuditReport
{
    /// <summary>工具名（固定字符串，不随版本/环境变化）。</summary>
    public string Tool { get; init; } = "cs2j dynamic-desugar";

    public required DynamicMode Mode { get; init; }

    public required DynamicAuditTotals Totals { get; init; }

    public required IReadOnlyList<DynamicAuditSiteEntry> Sites { get; init; }

    /// <summary>从已去重排序的清单构造报告（不读盘、不写时间戳）。</summary>
    public static DynamicAuditReport FromInventory(
        DynamicSiteInventory inventory,
        DynamicDesugarOptions options)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);

        var statistics = DynamicDesugarStatistics.FromRecords(inventory.Sites);
        var filesScanned = inventory.Sites.Select(site => site.FilePath).Distinct().Count();

        var entries = inventory.Sites
            .Select(site => new DynamicAuditSiteEntry
            {
                File = site.FilePath,
                Line = site.Line + 1,
                Column = site.Column + 1,
                Span = new[] { site.SpanStart, site.SpanEnd },
                Kind = site.Kind,
                Code = site.Code,
                SyntaxKind = site.SyntaxKindName,
                BoundSymbol = site.BoundSymbolName,
                Receiver = site.ReceiverText,
                Expression = site.ExpressionText,
                Provability = site.Provability,
                Reason = site.UnprovableReason,
                OriginType = site.OriginType,
                ManifestHit = site.ManifestHit,
                SymbolUnresolved = site.SymbolUnresolved,
                Message = site.Message,
            })
            .ToList();

        return new DynamicAuditReport
        {
            Mode = options.Mode,
            Totals = new DynamicAuditTotals
            {
                FilesScanned = filesScanned,
                Sites = statistics.SitesTotal,
                DeclarationsClassified = statistics.DeclarationsClassified,
                ProvenDeclarations = statistics.ProvenDeclarations,
                UnprovableSites = statistics.UnprovableSites,
                ManifestSuppressed = statistics.ManifestSuppressed,
                ManifestStaleEntries = statistics.ManifestStaleEntries,
                ResidualSites = statistics.ResidualSites,
                SitesByKind = statistics.SitesByKind,
            },
            Sites = entries,
        };
    }
}

/// <summary>
/// 报告 JSON 序列化（设计 §4.6）：WriteIndented；camelCase 属性与枚举；
/// UTF-8 无 BOM 写盘（字节稳定契约）。
/// </summary>
public sealed class DynamicAuditReportSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    public string Serialize(DynamicAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, SerializerOptions);
    }

    /// <summary>写盘为 UTF-8 无 BOM（字节稳定契约）。</summary>
    public void WriteToFile(string path, DynamicAuditReport report)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(report);
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
