using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>
/// platform-isolation 门面（设计 §4 组件表）。M1 只做审计：<see cref="BindCompilation"/>
/// 绑定整树，<see cref="BuildInventory"/> 整树一次缓存，<see cref="BuildReport"/>
/// 产出报告模型 + PI0xx 诊断 + 统计。apply 相关钩子（<see cref="RewriteFile"/>/
/// <see cref="CloseReport"/>）保留最小接口，Report 模式不产生任何字节改写
/// （M1 零行为变化；M2/M3 的 IsolationPlan/StubGenerator/FacadeGenerator 未实现）。
/// </summary>
public sealed class PlatformIsolationPreprocessor
{
    private Compilation? _compilation;
    private PlatformIsolationOptions _options = new();
    private bool _isTestProject;
    private IReadOnlyCollection<string>? _compatNamespacePrefixes;
    private PlatformIsolationConfig? _config;
    private bool _configLoaded;
    private BoundaryInventory? _inventory;

    public Compilation? Compilation => _compilation;

    public PlatformIsolationOptions Options => _options;

    /// <summary>已加载的源根配置（Cs2jPlatformIsolation.json；不存在时为 null）。</summary>
    public PlatformIsolationConfig? Config
    {
        get
        {
            EnsureConfigLoaded();
            return _config;
        }
    }

    public void BindCompilation(
        Compilation compilation,
        PlatformIsolationOptions? options = null,
        bool isTestProject = false,
        IReadOnlyCollection<string>? compatNamespacePrefixes = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _options = options ?? new PlatformIsolationOptions();
        _isTestProject = isTestProject;
        _compatNamespacePrefixes = compatNamespacePrefixes;
        _inventory = null;
        _config = null;
        _configLoaded = false;
    }

    /// <summary>整树一次缓存构建清单（含配置 allowlist 合并后的 compat 前缀）。</summary>
    public BoundaryInventory BuildInventory()
    {
        if (_inventory != null)
        {
            return _inventory;
        }

        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before BuildInventory.");
        }

        EnsureConfigLoaded();
        var prefixes = MergeCompatPrefixes(_compatNamespacePrefixes, _config?.Allowlist);
        _inventory = BoundaryInventoryBuilder.Build(_compilation, _isTestProject, prefixes);
        return _inventory;
    }

    /// <summary>构建报告（应用配置 policy/suppress；发射 PI001/060/061/070/090）。</summary>
    public PlatformIsolationReportBuildResult BuildReport()
    {
        EnsureConfigLoaded();
        return PlatformIsolationReportBuilder.Build(BuildInventory(), _options, _config);
    }

    /// <summary>
    /// M1 Report 模式恒返回 null（字节复制）。M2/M3 apply 模式在此接入
    /// IsolationPlan + Generator 链；本里程碑不实现。
    /// </summary>
    public string? RewriteFile(string filePath, string sourceText)
    {
        _ = filePath;
        _ = sourceText;
        return null;
    }

    /// <summary>M1 无跨文件产物可收口；M2/M3 在此分发 generated/ 文件。</summary>
    public void CloseReport()
    {
    }

    private void EnsureConfigLoaded()
    {
        if (_configLoaded)
        {
            return;
        }

        _config = PlatformIsolationConfigLoader.Load(_options.ConfigPath);
        _configLoaded = true;
    }

    private static IReadOnlyCollection<string>? MergeCompatPrefixes(
        IReadOnlyCollection<string>? compatPrefixes,
        IReadOnlyList<string>? allowlist)
    {
        if ((compatPrefixes == null || compatPrefixes.Count == 0)
            && (allowlist == null || allowlist.Count == 0))
        {
            return compatPrefixes;
        }

        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prefix in compatPrefixes ?? [])
        {
            if (seen.Add(prefix))
            {
                merged.Add(prefix);
            }
        }

        foreach (var prefix in allowlist ?? [])
        {
            if (seen.Add(prefix))
            {
                merged.Add(prefix);
            }
        }

        return merged;
    }
}