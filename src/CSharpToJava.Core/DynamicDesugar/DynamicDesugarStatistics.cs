namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// dynamic-desugar 计数器（设计 §4.6）。形态仿 JavaKeywordStatistics：可累加实例，
/// 供适配器逐文件 RewriteFile 传入；<see cref="FromRecords"/> 供报告侧从整树清单一次性构造。
/// </summary>
public sealed class DynamicDesugarStatistics
{
    public int FilesScanned { get; set; }

    public int FilesTransformed { get; set; }

    public int FilesSkippedClean { get; set; }

    public int SitesTotal { get; set; }

    /// <summary>按 kind 计数；字典在构造时预填全部 13 个 kind（零值），保证 JSON 字段稳定。</summary>
    public Dictionary<DynamicSiteKind, int> SitesByKind { get; } = CreateKindMap();

    public int DeclarationsClassified { get; set; }

    public int ProvenDeclarations { get; set; }

    public int DeclarationsRewritten { get; set; }

    public int UnprovableSites { get; set; }

    public int ManifestSuppressed { get; set; }

    public int ManifestStaleEntries { get; set; }

    public int ResidualSites { get; set; }

    public int ReportsWritten { get; set; }

    /// <summary>从去重排序后的站点清单构造统计（声明/分类计数，M1 口径）。</summary>
    public static DynamicDesugarStatistics FromRecords(IReadOnlyList<DynamicSiteRecord> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var statistics = new DynamicDesugarStatistics { SitesTotal = sites.Count };
        foreach (var site in sites)
        {
            statistics.SitesByKind[site.Kind]++;
            if (IsDeclarationKind(site.Kind))
            {
                statistics.DeclarationsClassified++;
            }

            if (site.Provability == DynamicProvability.ProvenP1)
            {
                statistics.ProvenDeclarations++;
            }

            if (site.UnprovableReason != null)
            {
                statistics.UnprovableSites++;
            }

            if (site.ManifestHit)
            {
                statistics.ManifestSuppressed++;
            }
        }

        return statistics;
    }

    /// <summary>累加单文件统计（适配器逐文件场景；SitesTotal 与 kind 明细按站点累加）。</summary>
    public void AddFile(IReadOnlyList<DynamicSiteRecord> fileSites, bool transformed)
    {
        ArgumentNullException.ThrowIfNull(fileSites);

        FilesScanned++;
        if (transformed)
        {
            FilesTransformed++;
        }
        else if (fileSites.Count == 0)
        {
            FilesSkippedClean++;
        }

        SitesTotal += fileSites.Count;
        foreach (var site in fileSites)
        {
            SitesByKind[site.Kind]++;
            if (IsDeclarationKind(site.Kind))
            {
                DeclarationsClassified++;
            }

            if (site.Provability == DynamicProvability.ProvenP1)
            {
                ProvenDeclarations++;
            }

            if (site.UnprovableReason != null)
            {
                UnprovableSites++;
            }

            if (site.ManifestHit)
            {
                ManifestSuppressed++;
            }
        }
    }

    internal static bool IsDeclarationKind(DynamicSiteKind kind)
        => kind is DynamicSiteKind.DynamicLocalDecl
            or DynamicSiteKind.DynamicParameter
            or DynamicSiteKind.DynamicReturn
            or DynamicSiteKind.DynamicFieldOrProperty;

    internal static Dictionary<DynamicSiteKind, int> CreateKindMap()
    {
        var map = new Dictionary<DynamicSiteKind, int>();
        foreach (DynamicSiteKind kind in Enum.GetValues(typeof(DynamicSiteKind)))
        {
            map[kind] = 0;
        }

        return map;
    }
}
