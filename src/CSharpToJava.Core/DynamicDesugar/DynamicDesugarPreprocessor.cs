using Microsoft.CodeAnalysis;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>单文件审计结果。M1 恒 <see cref="Changed"/>=false（字节复制）。</summary>
public sealed class DynamicFileResult
{
    public required string FilePath { get; init; }

    /// <summary>M1 Audit 恒 false；M1 选择 Rewrite 口径时仍不重写（只变诊断分级）。</summary>
    public required bool Changed { get; init; }

    public required string OutputCode { get; init; }

    /// <summary>本文件命中的站点（清单顺序）。</summary>
    public required IReadOnlyList<DynamicSiteRecord> FileSites { get; init; }

    public required IReadOnlyList<DynamicDesugarDiagnostic> Diagnostics { get; init; }
}

/// <summary>整树内存批处理结果（单测/批处理入口）。</summary>
public sealed class DynamicApplyResult
{
    public required IReadOnlyList<DynamicFileResult> Files { get; init; }

    public required IReadOnlyList<DynamicDesugarDiagnostic> Diagnostics { get; init; }

    public required DynamicDesugarStatistics Statistics { get; init; }

    public required bool HasErrors { get; init; }
}

/// <summary>
/// C25 dynamic 去糖门面（设计 §4.1/§4.3/§4.6）。M1 只交付纯审计：整树一次构建
/// <see cref="DynamicSiteInventory"/>；<see cref="RewriteFile"/> 对适配器逐文件返回
/// 具名 DS0xx 诊断且字节不变；<see cref="CloseReport"/> 产出字节稳定的
/// <see cref="DynamicAuditReport"/>。M1 不接线进 PassCatalog/PassChainFactory。
/// </summary>
public sealed class DynamicDesugarPreprocessor
{
    private Compilation? _compilation;
    private DynamicDesugarOptions _options;
    private DynamicSiteInventory? _inventory;

    public DynamicDesugarPreprocessor(DynamicDesugarOptions? options = null)
    {
        _options = options ?? new DynamicDesugarOptions();
    }

    public Compilation? Compilation => _compilation;

    public DynamicDesugarOptions Options => _options;

    /// <summary>绑定编译并重置所有缓存（选项可在此覆盖构造期默认值）。</summary>
    public void BindCompilation(Compilation compilation, DynamicDesugarOptions? options = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _options = options ?? _options;
        _inventory = null;
        HasErrors = false;
    }

    /// <summary>整树一次缓存构建清单。</summary>
    public DynamicSiteInventory BuildInventory()
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

        _inventory = DynamicSiteInventoryBuilder.Build(_compilation);
        return _inventory;
    }

    /// <summary>
    /// 单文件审计（适配器标准 spine）。Audit/rewrite 两种口径 M1 均不改源码
    /// （Changed=false、字节复制）；差异仅在诊断分级。
    /// </summary>
    public DynamicFileResult RewriteFile(
        string filePath,
        string sourceCode,
        SemanticModel? semanticModel = null)
        => RewriteFileCore(filePath, sourceCode, statistics: null, diagnosticsSink: null);

    /// <summary>
    /// JK 风格逐文件重载：把本文件统计累加进 <paramref name="statistics"/>、
    /// 诊断追加进 <paramref name="diagnosticsSink"/>（任一为 null 表示不收集）。
    /// </summary>
    public DynamicFileResult RewriteFile(
        string filePath,
        string sourceCode,
        SemanticModel? semanticModel,
        DynamicDesugarStatistics statistics,
        IList<DynamicDesugarDiagnostic> diagnosticsSink)
        => RewriteFileCore(filePath, sourceCode, statistics, diagnosticsSink);

    /// <summary>
    /// 构造（不写盘）审计报告；同一编译上多次调用序列化结果逐字节一致
    /// （无时间戳/GUID/机器名）。
    /// </summary>
    public DynamicAuditReport CloseReport()
        => DynamicAuditReport.FromInventory(BuildInventory(), _options);

    /// <summary>
    /// 整树内存批处理：逐树 <see cref="RewriteFile"/>，汇总统计与诊断。
    /// </summary>
    public DynamicApplyResult RewriteAll()
    {
        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before RewriteAll.");
        }

        var inventory = BuildInventory();
        var files = new List<DynamicFileResult>();
        var diagnostics = new List<DynamicDesugarDiagnostic>();

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            var result = RewriteFileCore(
                tree.FilePath ?? string.Empty,
                text,
                statistics: null,
                diagnosticsSink: diagnostics);
            files.Add(result);
        }

        // 站点口径以清单为准（FromRecords），文件口径按实际遍历树数覆盖。
        var statistics = DynamicDesugarStatistics.FromRecords(inventory.Sites);
        statistics.FilesScanned = files.Count;
        statistics.FilesSkippedClean = files.Count(result => result.FileSites.Count == 0);
        return new DynamicApplyResult
        {
            Files = files,
            Diagnostics = diagnostics,
            Statistics = statistics,
            HasErrors = HasErrors
                       || diagnostics.Any(d => d.Severity == DynamicDesugarSeverity.Error),
        };
    }

    /// <summary>累积错误态：任一已发射诊断为 Error 即 true（只单调置位）。</summary>
    public bool HasErrors { get; private set; }

    private DynamicFileResult RewriteFileCore(
        string filePath,
        string sourceCode,
        DynamicDesugarStatistics? statistics,
        IList<DynamicDesugarDiagnostic>? diagnosticsSink)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(sourceCode);

        var fileSites = BuildInventory().ForFile(filePath);
        var diagnostics = new List<DynamicDesugarDiagnostic>();

        foreach (var site in fileSites)
        {
            // null = 该站点不发射诊断（基线错误保守台账不叠加编译器噪声）。
            var severity = DynamicDesugarSeverityPolicy.Resolve(site, _options);
            if (severity == null)
            {
                continue;
            }

            var diagnostic = new DynamicDesugarDiagnostic(
                site.Code,
                severity.Value,
                site.Message,
                filePath,
                site.Line + 1,
                site.Column + 1);
            diagnostics.Add(diagnostic);
            diagnosticsSink?.Add(diagnostic);
            if (severity == DynamicDesugarSeverity.Error)
            {
                HasErrors = true;
            }
        }

        statistics?.AddFile(fileSites, transformed: false);

        return new DynamicFileResult
        {
            FilePath = filePath,
            Changed = false,
            OutputCode = sourceCode,
            FileSites = fileSites,
            Diagnostics = diagnostics,
        };
    }
}
