namespace CSharpToJava.Core.VarianceUseSite;

/// <summary>variance-use-site-normalize 全链统计（summary 行与报告 totals 共用）。</summary>
public sealed class VarianceUseSiteNormalizeStatistics
{
    public int FilesScanned { get; set; }
    public int FilesTransformed { get; set; }
    public int FilesSkippedClean { get; set; }

    /// <summary>per-file 校验失败而整文件回滚的次数。</summary>
    public int FilesRolledBack { get; set; }

    public int SitesTotal { get; set; }
    public int SitesRewritten { get; set; }
    public int SitesPreserved { get; set; }
    public int SitesDeclaredUnsupported { get; set; }

    /// <summary>VR-xx → 改写站点数（仅改写动作）。</summary>
    public SortedDictionary<string, int> RewrittenByRule { get; } = new(StringComparer.Ordinal);

    /// <summary>VR-xx → 保留站点数（VR-04）。</summary>
    public SortedDictionary<string, int> PreservedByRule { get; } = new(StringComparer.Ordinal);

    /// <summary>VR-xx → DeclaredUnsupported 站点数。</summary>
    public SortedDictionary<string, int> UnsupportedByRule { get; } = new(StringComparer.Ordinal);

    public void Add(VarianceUseSiteRecord record)
    {
        SitesTotal++;
        switch (record.Action)
        {
            case VarianceUseSiteActions.Rewritten:
                SitesRewritten++;
                Increment(RewrittenByRule, record.Rule);
                break;
            case VarianceUseSiteActions.Preserved:
                SitesPreserved++;
                Increment(PreservedByRule, record.Rule);
                break;
            default:
                SitesDeclaredUnsupported++;
                Increment(UnsupportedByRule, record.Rule);
                break;
        }
    }

    private static void Increment(SortedDictionary<string, int> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
    }

    public string SummaryLine()
        => $"variance-use-site-normalize: {FilesTransformed} transformed, " +
           $"{FilesSkippedClean} clean, {SitesRewritten} rewritten " +
           $"[{Format(RewrittenByRule)}], {SitesPreserved} preserved " +
           $"[{Format(PreservedByRule)}], {SitesDeclaredUnsupported} unsupported " +
           $"[{Format(UnsupportedByRule)}], {FilesRolledBack} rollback";

    private static string Format(SortedDictionary<string, int> counts)
        => counts.Count == 0
            ? "-"
            : string.Join(",", counts.Select(pair => $"{pair.Key}={pair.Value}"));
}
