using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CoreDiagnosticSeverity = CSharpToJava.Core.Context.DiagnosticSeverity;

namespace CSharpToJava.Core.PlatformIsolation;

/// <summary>单文件 apply 结果（Changed=false 即字节复制）。</summary>
public sealed class PlatformIsolationFileResult
{
    public required string FilePath { get; init; }
    public required bool Changed { get; init; }
    public required string OutputCode { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
}

/// <summary>批量 apply 结果（含生成文件元组与 residual 闸门，设计 §3.4）。</summary>
public sealed class PlatformIsolationApplyResult
{
    public required IReadOnlyList<PlatformIsolationFileResult> Files { get; init; }
    public required IReadOnlyList<GeneratedFile> GeneratedFiles { get; init; }
    public required IReadOnlyList<PlatformIsolationDiagnostic> Diagnostics { get; init; }
    public required PlatformIsolationStatistics Statistics { get; init; }
    public required int ResidualCount { get; init; }
    public required bool HasErrors { get; init; }
}

/// <summary>
/// platform-isolation 门面（设计 §4 组件表）。Report 模式只审计；M2 apply 模式
/// （Reject/Stub/Facade）走 IsolationPlan + StubGenerator + residual 闸门。<see cref="RewriteFile"/>
/// 供适配器标准 spine 逐文件调用，<see cref="RewriteAll"/> 供整树内存批处理与单测。
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
    private IsolationPlan? _plan;
    private readonly List<GeneratedFile> _generatedFiles = [];
    private bool _probeShimEmitted;

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

    /// <summary>已累积的生成文件元组（相对路径, 文本）。</summary>
    public IReadOnlyList<GeneratedFile> GeneratedFiles => _generatedFiles;

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
        _plan = null;
        _config = null;
        _configLoaded = false;
        _generatedFiles.Clear();
        _probeShimEmitted = false;
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

    /// <summary>整树一次缓存裁决动作表（设计 §4.7）。</summary>
    public IsolationPlan BuildIsolationPlan()
    {
        if (_plan != null)
        {
            return _plan;
        }

        EnsureConfigLoaded();
        _plan = IsolationPlanner.Build(BuildInventory(), _options, _config);
        return _plan;
    }

    /// <summary>Report 模式报告（应用配置 policy/suppress；发射 PI001/060/061/070/090）。</summary>
    public PlatformIsolationReportBuildResult BuildReport()
    {
        EnsureConfigLoaded();
        return PlatformIsolationReportBuilder.Build(BuildInventory(), _options, _config);
    }

    /// <summary>
    /// 单文件 apply（供适配器逐文件调用）。Report 模式恒不变；Reject 模式不改写、
    /// 对每个 Reject/Facade 站点发 PI030 Error；Stub/Facade 模式执行桩生成。
    /// </summary>
    public PlatformIsolationFileResult RewriteFile(
        string filePath,
        string sourceText,
        SemanticModel? semanticModel = null)
    {
        EnsureConfigLoaded();
        var plan = BuildIsolationPlan();
        var actions = plan.ForFile(filePath).ToList();
        var diagnostics = new List<PlatformIsolationDiagnostic>();

        if (_options.Mode == PlatformIsolationMode.Report || actions.Count == 0)
        {
            return Unchanged(filePath, sourceText, diagnostics);
        }

        foreach (var action in actions)
        {
            if (action.Action == PlatformIsolationAction.Reject)
            {
                diagnostics.Add(BuildRejectDiagnostic(action, filePath));
            }
            else if (action.Action == PlatformIsolationAction.Facade)
            {
                diagnostics.Add(new PlatformIsolationDiagnostic(
                    PlatformIsolationDiagnosticCodes.RejectExecuted,
                    CoreDiagnosticSeverity.Error,
                    $"Facade isolation for '{action.Site.Identity}' at " +
                    $"{filePath}({action.Site.Line + 1},{action.Site.Column}) is not implemented until C42 M3.",
                    filePath));
            }
            else if (action.Action == PlatformIsolationAction.Suppressed && action.ConfigMatch != null)
            {
                diagnostics.Add(new PlatformIsolationDiagnostic(
                    PlatformIsolationDiagnosticCodes.ConfigSuppressed,
                    _options.Strict ? CoreDiagnosticSeverity.Warning : CoreDiagnosticSeverity.Info,
                    $"Platform boundary site '{action.Site.Identity}' at " +
                    $"{filePath}({action.Site.Line + 1},{action.Site.Column}) suppressed by config.",
                    filePath));
            }
        }

        if (_options.Mode == PlatformIsolationMode.Reject)
        {
            return Unchanged(filePath, sourceText, diagnostics);
        }

        var isAssemblyInfo = BoundaryAnalysisHelpers.IsGeneratedAssemblyInfoFile(filePath);
        var rewrite = StubGenerator.Rewrite(filePath, sourceText, semanticModel, plan, isAssemblyInfo);
        diagnostics.AddRange(rewrite.Diagnostics);

        if (rewrite.ProbeRedirected)
        {
            EnsureProbeShim();
        }

        return new PlatformIsolationFileResult
        {
            FilePath = filePath,
            Changed = rewrite.Changed,
            OutputCode = rewrite.OutputCode,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// 整树内存批处理（单测 + 适配器批处理入口）：逐树 apply → residual 闸门。
    /// </summary>
    public PlatformIsolationApplyResult RewriteAll()
    {
        if (_compilation == null)
        {
            throw new InvalidOperationException(
                "BindCompilation must be called before RewriteAll.");
        }

        EnsureConfigLoaded();
        var plan = BuildIsolationPlan();
        var files = new List<PlatformIsolationFileResult>();
        var diagnostics = new List<PlatformIsolationDiagnostic>();

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            var model = _compilation.GetSemanticModel(tree);
            var result = RewriteFile(tree.FilePath ?? string.Empty, text, model);
            files.Add(result);
            diagnostics.AddRange(result.Diagnostics);
        }

        foreach (var stale in plan.StaleConfigEntries)
        {
            diagnostics.Add(new PlatformIsolationDiagnostic(
                PlatformIsolationDiagnosticCodes.StaleConfigEntry,
                CoreDiagnosticSeverity.Warning,
                $"Stale platform-isolation config entry (no matching site): {stale}."));
        }

        var residualCount = 0;
        if (_options.Mode is PlatformIsolationMode.Stub or PlatformIsolationMode.Facade)
        {
            var outputCompilation = BuildOutputCompilation(files);
            var gate = PlatformResidualGate.Scan(outputCompilation, IsGeneratedPath, _config);
            residualCount = gate.ResidualCount;
            diagnostics.AddRange(gate.Diagnostics);
        }

        var hasErrors = diagnostics.Any(d => d.Severity == CoreDiagnosticSeverity.Error)
                        || residualCount > 0;

        return new PlatformIsolationApplyResult
        {
            Files = files,
            GeneratedFiles = _generatedFiles.ToList(),
            Diagnostics = diagnostics,
            Statistics = plan.Statistics,
            ResidualCount = residualCount,
            HasErrors = hasErrors,
        };
    }

    /// <summary>M2 收口：返回累积的生成文件（供适配器 AfterFiles 分发）。</summary>
    public IReadOnlyList<GeneratedFile> CloseReport() => _generatedFiles.ToList();

    public bool HasErrors { get; private set; }

    private PlatformIsolationFileResult Unchanged(
        string filePath,
        string sourceText,
        IReadOnlyList<PlatformIsolationDiagnostic> diagnostics)
    {
        MarkErrors(diagnostics);
        return new PlatformIsolationFileResult
        {
            FilePath = filePath,
            Changed = false,
            OutputCode = sourceText,
            Diagnostics = diagnostics,
        };
    }

    private void MarkErrors(IReadOnlyList<PlatformIsolationDiagnostic> diagnostics)
    {
        if (diagnostics.Any(d => d.Severity == CoreDiagnosticSeverity.Error))
        {
            HasErrors = true;
        }
    }

    private static PlatformIsolationDiagnostic BuildRejectDiagnostic(IsolationAction action, string filePath)
        => new(
            PlatformIsolationDiagnosticCodes.RejectExecuted,
            CoreDiagnosticSeverity.Error,
            $"Reject {action.Site.Code} '{action.Site.Identity}' at " +
            $"{filePath}({action.Site.Line + 1},{action.Site.Column}): {action.Reason}; " +
            "provide a compat mapping, a manual port, or a config suppress entry " +
            "(interop whitelist belongs to C06).",
            filePath);

    private void EnsureProbeShim()
    {
        if (_probeShimEmitted)
        {
            return;
        }

        _probeShimEmitted = true;
        _generatedFiles.Add(new GeneratedFile
        {
            RelativePath = "generated/" + StubGenerator.ProbeShimFileName,
            Content = StubGenerator.BuildProbeShim(),
        });
    }

    private static bool IsGeneratedPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/generated/", StringComparison.Ordinal)
               || normalized.EndsWith(".g.cs", StringComparison.Ordinal);
    }

    private CSharpCompilation BuildOutputCompilation(IReadOnlyList<PlatformIsolationFileResult> files)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var file in files)
        {
            trees.Add(CSharpSyntaxTree.ParseText(file.OutputCode, parseOptions, path: file.FilePath));
        }

        foreach (var generated in _generatedFiles)
        {
            trees.Add(CSharpSyntaxTree.ParseText(generated.Content, parseOptions, path: generated.RelativePath));
        }

        return CSharpCompilation.Create(
            "platform-isolation-output",
            trees,
            ProjectCompilationBuilder.GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
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