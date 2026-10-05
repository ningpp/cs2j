using System.Text.Json;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// manifest 加载/结构非法时 fail-fast 抛出（设计 D13）。文件不存在不抛——
/// 视为空清单；只有「文件在但读不懂」才抛，由 RewriteAll 转成明确的
/// DS030 Error 诊断（最小方案：不新增码位，DS030 是闸门失败统一出口）。
/// </summary>
public sealed class DynamicAuditManifestException : Exception
{
    public DynamicAuditManifestException(string message)
        : base(message)
    {
    }

    public DynamicAuditManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// C25 审计登记 manifest 单条目（D13，文件名 Cs2jDynamicAudit.json，落源根、
/// 随源码受控）。匹配身份为 file + kind + span 相交 + symbol（条目提供时）；
/// line/column/auditedAt 仅展示与评审用途，不参与匹配。
/// </summary>
public sealed class DynamicAuditManifestEntry
{
    /// <summary>源根相对路径（'/' 与 '\\' 匹配前统一为 '/'）。</summary>
    public required string File { get; init; }

    /// <summary>1 基行号（展示用，不影响匹配）。</summary>
    public required int Line { get; init; }

    /// <summary>1 基列号（可选；展示用，不影响匹配）。</summary>
    public int? Column { get; init; }

    /// <summary>span 起点（字符偏移，0 基）。</summary>
    public required int SpanStart { get; init; }

    /// <summary>span 长度（正数；与站点 span 相等或相交即命中）。</summary>
    public required int SpanLength { get; init; }

    /// <summary>站点 kind（<see cref="DynamicSiteKind"/> 枚举名，大小写不敏感）。</summary>
    public required DynamicSiteKind Kind { get; init; }

    /// <summary>绑定符号显示名；提供时必须与站点符号相等，null/空表示不校验符号。</summary>
    public string? Symbol { get; init; }

    /// <summary>人工审计说明（必填非空；防腐烂，R7）。</summary>
    public required string Reason { get; init; }

    /// <summary>审计人（必填非空；D13 留痕要求）。</summary>
    public required string Auditor { get; init; }

    /// <summary>审计日期/时间戳（仅展示用，不参与匹配；不要求格式、不影响字节稳定）。</summary>
    public string? AuditedAt { get; init; }

    /// <summary>span 终点（不含）。</summary>
    public int SpanEnd => SpanStart + SpanLength;
}

/// <summary>
/// 文档级 manifest 模型：<c>entries</c> 数组 + 可选注释元数据（<c>comment</c>，
/// 额外字段容忍）。加载入口 <see cref="Load"/>。
/// </summary>
public sealed class DynamicAuditManifest
{
    /// <summary>可选文档注释（人工说明本清单范围；纯展示）。</summary>
    public string? Comment { get; init; }

    /// <summary>登记条目（保持文件内顺序；陈旧诊断按此顺序发射）。</summary>
    public required IReadOnlyList<DynamicAuditManifestEntry> Entries { get; init; }

    /// <summary>实际加载路径（null 表示无 manifest/空清单；供基目录推导与诊断）。</summary>
    public string? LoadedPath { get; init; }

    /// <summary>
    /// 加载 manifest：路径为 null/空或文件不存在 → 空清单（合法零登记态）；
    /// JSON 非法或条目缺必填字段 → <see cref="DynamicAuditManifestException"/>
    /// fail-fast（让 RewriteAll/适配器产出明确 error 诊断）。
    /// </summary>
    public static DynamicAuditManifest Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new DynamicAuditManifest { Entries = [], LoadedPath = path };
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new DynamicAuditManifestException(
                $"Failed to read dynamic audit manifest '{path}': {ex.Message}", ex);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            throw new DynamicAuditManifestException(
                $"Dynamic audit manifest '{path}' is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DynamicAuditManifestException(
                    $"Dynamic audit manifest '{path}' must be a JSON object "
                    + "with an 'entries' array.");
            }

            if (!document.RootElement.TryGetProperty("entries", out var entriesElement))
            {
                throw new DynamicAuditManifestException(
                    $"Dynamic audit manifest '{path}' is missing the required 'entries' array.");
            }

            if (entriesElement.ValueKind != JsonValueKind.Array)
            {
                throw new DynamicAuditManifestException(
                    $"Dynamic audit manifest '{path}': 'entries' must be a JSON array.");
            }

            string? comment = null;
            if (document.RootElement.TryGetProperty("comment", out var commentElement)
                && commentElement.ValueKind == JsonValueKind.String)
            {
                comment = commentElement.GetString();
            }

            var entries = new List<DynamicAuditManifestEntry>();
            var index = 0;
            foreach (var element in entriesElement.EnumerateArray())
            {
                entries.Add(ParseEntry(element, path, index));
                index++;
            }

            return new DynamicAuditManifest
            {
                Comment = comment,
                Entries = entries,
                LoadedPath = path,
            };
        }
    }

    private static DynamicAuditManifestEntry ParseEntry(
        JsonElement element,
        string manifestPath,
        int index)
    {
        var where = $"Dynamic audit manifest '{manifestPath}' entry[{index}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new DynamicAuditManifestException($"{where} must be a JSON object.");
        }

        var file = RequireString(element, "file", where, nonEmpty: true);
        var line = RequireInt(element, "line", where, min: 1);
        var spanStart = RequireInt(element, "spanStart", where, min: 0);
        var spanLength = RequireInt(element, "spanLength", where, min: 1);
        var kindText = RequireString(element, "kind", where, nonEmpty: true);
        var reason = RequireString(element, "reason", where, nonEmpty: true);
        var auditor = RequireString(element, "auditor", where, nonEmpty: true);

        if (!Enum.TryParse<DynamicSiteKind>(kindText, ignoreCase: true, out var kind)
            || !Enum.IsDefined(kind))
        {
            throw new DynamicAuditManifestException(
                $"{where}: unknown kind '{kindText}'. Expected one of: "
                + string.Join(", ", Enum.GetNames<DynamicSiteKind>()) + ".");
        }

        int? column = null;
        if (element.TryGetProperty("column", out var columnElement)
            && columnElement.ValueKind != JsonValueKind.Null)
        {
            column = RequireInt(element, "column", where, min: 1);
        }

        string? symbol = null;
        if (element.TryGetProperty("symbol", out var symbolElement)
            && symbolElement.ValueKind == JsonValueKind.String)
        {
            var text = symbolElement.GetString();
            symbol = string.IsNullOrWhiteSpace(text) ? null : text;
        }

        string? auditedAt = null;
        if (element.TryGetProperty("auditedAt", out var auditedAtElement)
            && auditedAtElement.ValueKind == JsonValueKind.String)
        {
            auditedAt = auditedAtElement.GetString();
        }

        return new DynamicAuditManifestEntry
        {
            File = NormalizeFile(file),
            Line = line,
            Column = column,
            SpanStart = spanStart,
            SpanLength = spanLength,
            Kind = kind,
            Symbol = symbol,
            Reason = reason,
            Auditor = auditor,
            AuditedAt = auditedAt,
        };
    }

    private static string RequireString(
        JsonElement element,
        string name,
        string where,
        bool nonEmpty)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            throw new DynamicAuditManifestException($"{where} is missing required string '{name}'.");
        }

        var value = property.GetString() ?? string.Empty;
        if (nonEmpty && string.IsNullOrWhiteSpace(value))
        {
            throw new DynamicAuditManifestException($"{where}: '{name}' must be non-empty.");
        }

        return value;
    }

    private static int RequireInt(
        JsonElement element,
        string name,
        string where,
        int min)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value))
        {
            throw new DynamicAuditManifestException(
                $"{where} is missing required integer '{name}'.");
        }

        if (value < min)
        {
            throw new DynamicAuditManifestException(
                $"{where}: '{name}' must be >= {min} but was {value}.");
        }

        return value;
    }

    /// <summary>'\\' → '/'（条目侧；站点侧匹配时同口径归一）。</summary>
    internal static string NormalizeFile(string file) => file.Replace('\\', '/');

    /// <summary>
    /// 条目是否命中一个输出残余站点：file 与 kind 相等、span 相交（相等或包含；
    /// 半开区间）、条目提供 symbol 时符号也要相等。
    /// </summary>
    /// <remarks>
    /// 前提（设计 D13）：登记站点实际都在不被 A1 具体化的字节复制文件里
    /// （参数/返回/字段/Expando 等永不自动重写，见 DynamicSiteRewriter），
    /// 其 span 在改写前后稳定；被具体化文件的使用点随声明一起静态化、不会
    /// 成为残余，故 span 相交而非逐字符相等已足够抗格式化微移。
    /// </remarks>
    internal static bool MatchesSite(
        DynamicAuditManifestEntry entry,
        DynamicSiteRecord site,
        IReadOnlyCollection<string> siteFileCandidates)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(siteFileCandidates);

        if (entry.Kind != site.Kind)
        {
            return false;
        }

        // 半开区间相交：aStart < bEnd && bStart < aEnd。
        if (entry.SpanStart >= site.SpanEnd || site.SpanStart >= entry.SpanEnd)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(entry.Symbol)
            && !string.Equals(entry.Symbol, site.BoundSymbolName, StringComparison.Ordinal))
        {
            return false;
        }

        var entryFile = NormalizeFile(entry.File);
        foreach (var candidate in siteFileCandidates)
        {
            if (string.Equals(entryFile, NormalizeFile(candidate), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
