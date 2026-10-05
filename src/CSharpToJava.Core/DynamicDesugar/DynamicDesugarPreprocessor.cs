using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>单文件审计/重写结果。Audit 恒 <see cref="Changed"/>=false（字节复制）；
/// Rewrite 仅在 A1 声明具体化通过三候选校验时 Changed=true。</summary>
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
    private FlowTypeProof? _proof;

    public DynamicDesugarPreprocessor(DynamicDesugarOptions? options = null)
    {
        _options = options ?? new DynamicDesugarOptions();
    }

    public Compilation? Compilation => _compilation;

    public DynamicDesugarOptions Options => _options;

    /// <summary>
    /// 测试钩子（InternalsVisibleTo）：替换三候选校验器（全失败保护/候选兜底用例），
    /// 仿 ExtensionMethodResolvePreprocessor 的注入形态。生产路径恒为 null（默认校验器）。
    /// </summary>
    internal DynamicRewriteValidator? RewriteValidator { get; set; }

    /// <summary>绑定编译并重置所有缓存（选项可在此覆盖构造期默认值）。</summary>
    public void BindCompilation(Compilation compilation, DynamicDesugarOptions? options = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _options = options ?? _options;
        _inventory = null;
        _proof = null;
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

        var raw = DynamicSiteInventoryBuilder.Build(_compilation);
        _proof = FlowTypeProver.Classify(_compilation, raw, _options);
        _inventory = ApplyFlowProof(raw, _proof);
        return _inventory;
    }

    /// <summary>
    /// M2：清单建成后运行 P1 证明（设计 §4.4），把每个 DynamicLocalDecl 站点回写为
    /// DS001（ProvenP1）或 DS010（Unprovable + 具名原因）；proven T 显示名优先写入
    /// OriginType。其余站点原样透传，排序不变。
    /// </summary>
    private static DynamicSiteInventory ApplyFlowProof(
        DynamicSiteInventory raw,
        FlowTypeProof proof)
    {
        var classifications = new Dictionary<DynamicSiteRecord, FlowLocalClassification>(
            ReferenceEqualityComparer.Instance);
        foreach (var classification in proof.Classifications)
        {
            classifications[classification.Site] = classification;
        }

        var sites = raw.Sites
            .Select(site => classifications.TryGetValue(site, out var classification)
                ? RewriteLocalSite(site, classification)
                : site)
            .ToList();
        return new DynamicSiteInventory { Sites = sites };
    }

    private static DynamicSiteRecord RewriteLocalSite(
        DynamicSiteRecord site,
        FlowLocalClassification classification)
    {
        var proven = classification.Provability == DynamicProvability.ProvenP1;
        return new DynamicSiteRecord
        {
            Kind = site.Kind,
            FilePath = site.FilePath,
            SpanStart = site.SpanStart,
            SpanEnd = site.SpanEnd,
            Line = site.Line,
            Column = site.Column,
            SyntaxKindName = site.SyntaxKindName,
            ExpressionText = site.ExpressionText,
            ReceiverText = site.ReceiverText,
            BoundSymbolName = site.BoundSymbolName,
            // proven T 优先；不可证时保留 M1 初始化器来源类型（报告参考）。
            OriginType = proven ? classification.ProvenTypeDisplay : site.OriginType,
            Provability = classification.Provability,
            UnprovableReason = classification.UnprovableReason,
            Code = proven
                ? DynamicDesugarDiagnosticCodes.ProvenConcretized
                : DynamicDesugarDiagnosticCodes.LocalUnprovable,
            Message = classification.Message,
            ManifestHit = site.ManifestHit,
            SymbolUnresolved = site.SymbolUnresolved,
        };
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

        BuildInventory();
        var files = new List<DynamicFileResult>();
        var diagnostics = new List<DynamicDesugarDiagnostic>();

        // 逐文件累加同一统计实例（站点/声明口径与 FromRecords 等价，文件口径真实取数：
        // FilesTransformed/FilesSkippedClean/DeclarationsRewritten）。
        var statistics = new DynamicDesugarStatistics();

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var text = tree.GetText().ToString();
            var result = RewriteFileCore(
                tree.FilePath ?? string.Empty,
                text,
                statistics,
                diagnostics);
            files.Add(result);
        }

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

        var inventory = BuildInventory();
        var fileSites = inventory.ForFile(filePath);

        // Audit 模式：M1 路径逐字节保持（消息、分级、字节复制均不变）。
        if (_options.Mode != DynamicMode.Rewrite)
        {
            var auditDiagnostics = EmitSiteDiagnostics(
                fileSites, filePath, concreteSites: null, diagnosticsSink);
            statistics?.AddFile(fileSites, transformed: false);
            return new DynamicFileResult
            {
                FilePath = filePath,
                Changed = false,
                OutputCode = sourceCode,
                FileSites = fileSites,
                Diagnostics = auditDiagnostics,
            };
        }

        // ---- Rewrite 模式 ----
        var tree = _compilation!.SyntaxTrees.FirstOrDefault(t => t.FilePath == filePath);
        var provenInFile = tree != null
            ? (_proof?.ProvenDeclarations ?? [])
                .Where(p => p.Declarator.SyntaxTree == tree)
                .ToList()
            : [];

        // dirty 快检：无 proven 时，若连 dynamic token/LateBound 语义节点都没有，
        // 即干净文件（CandidateReason 枚举中不存在 Dynamic 成员，晚绑定一律为 LateBound）。
        if (provenInFile.Count == 0 && tree != null)
        {
            var quickModel = _compilation.GetSemanticModel(tree);
            var dirty = fileSites.Count > 0
                        || DynamicSiteRewriter.IsDynamicDirty(tree.GetRoot(), quickModel);
            if (!dirty)
            {
                statistics?.AddFile(fileSites, transformed: false);
                return new DynamicFileResult
                {
                    FilePath = filePath,
                    Changed = false,
                    OutputCode = sourceCode,
                    FileSites = fileSites,
                    Diagnostics = [],
                };
            }
        }

        // 有未证站点（含未到重写门槛的 dynamic 文件）：分级照常发射，字节保留。
        if (provenInFile.Count == 0)
        {
            var unprovenDiagnostics = EmitSiteDiagnostics(
                fileSites, filePath, concreteSites: null, diagnosticsSink);
            statistics?.AddFile(fileSites, transformed: false);
            return new DynamicFileResult
            {
                FilePath = filePath,
                Changed = false,
                OutputCode = sourceCode,
                FileSites = fileSites,
                Diagnostics = unprovenDiagnostics,
            };
        }

        // A1 具体化：声明类型位替换 → 三候选重新 parse + 同口径编译校验。
        var targetTree = tree!;
        var csharpCompilation = (CSharpCompilation)_compilation;
        var semanticModel = csharpCompilation.GetSemanticModel(targetTree);
        var applyResult = DynamicSiteRewriter.Apply(semanticModel, provenInFile);

        var concreteSites = BuildConcreteSiteSet(
            fileSites, applyResult.ConcretizedDeclarators);
        var rewrittenText = DynamicSiteRewriter.FormatAndValidate(
            applyResult.Root,
            targetTree,
            csharpCompilation,
            RewriteValidator);

        if (rewrittenText == null)
        {
            // 三候选全败：原文保留 + Changed=false + DS040 Error（fail-closed）。
            var failureDiagnostics = EmitSiteDiagnostics(
                fileSites, filePath, concreteSites: null, diagnosticsSink);
            var firstSite = fileSites.FirstOrDefault(s => concreteSites.Contains(s));
            var locationText = firstSite != null
                ? $"line {firstSite.Line + 1}, col {firstSite.Column + 1}"
                : "unknown location";
            EmitDiagnostic(
                new DynamicDesugarDiagnostic(
                    DynamicDesugarDiagnosticCodes.RewriteSyntaxInvalid,
                    DynamicDesugarSeverity.Error,
                    $"A1 dynamic declaration concretization in '{filePath}' failed three-candidate "
                    + "validation (changed-span formatted, whole-file formatted, unformatted): "
                    + "every candidate introduced new compile errors; source retained byte-for-byte. "
                    + $"First concretized declaration at {locationText}.",
                    filePath,
                    firstSite?.Line + 1 ?? 1,
                    firstSite?.Column + 1 ?? 1),
                failureDiagnostics,
                diagnosticsSink);
            statistics?.AddFile(fileSites, transformed: false);
            return new DynamicFileResult
            {
                FilePath = filePath,
                Changed = false,
                OutputCode = sourceCode,
                FileSites = fileSites,
                Diagnostics = failureDiagnostics,
            };
        }

        // 成功：BOM 保持。输入以 U+FEFF 开头则输出以同一 BOM 开头；若原 BOM 被
        // lexer 当作编译单元前导 trivia 保留，候选文本会自带一个，先剥一个再补，避免双 BOM。
        var hasBom = sourceCode.Length > 0 && sourceCode[0] == '\uFEFF';
        var candidateBody = hasBom
            && rewrittenText.Length > 0
            && rewrittenText[0] == '\uFEFF'
                ? rewrittenText[1..]
                : rewrittenText;
        var outputCode = hasBom ? '\uFEFF' + candidateBody : candidateBody;

        var successDiagnostics = EmitSiteDiagnostics(
            fileSites, filePath, concreteSites, diagnosticsSink);
        statistics?.AddFile(fileSites, transformed: true);
        if (statistics != null)
        {
            statistics.DeclarationsRewritten += applyResult.DeclarationsRewritten;
        }

        return new DynamicFileResult
        {
            FilePath = filePath,
            Changed = true,
            OutputCode = outputCode,
            FileSites = fileSites,
            Diagnostics = successDiagnostics,
        };
    }

    /// <summary>
    /// 从清单站点中筛出"声明符节点已被本次具体化"的站点。ApplyFlowProof 会用新记录
    /// 实例替换原站点（classification.Site 指向旧 raw 记录），故按 (文件, span) 匹配
    /// 回 fileSites 的现行实例，再以引用相等集合返回。
    /// </summary>
    private HashSet<DynamicSiteRecord> BuildConcreteSiteSet(
        IReadOnlyList<DynamicSiteRecord> fileSites,
        HashSet<SyntaxNode> concretizedDeclarators)
    {
        var concreteSpans = new HashSet<(string FilePath, int SpanStart, int SpanEnd)>();
        if (_proof != null)
        {
            foreach (var classification in _proof.Classifications)
            {
                if (classification.Provability == DynamicProvability.ProvenP1
                    && concretizedDeclarators.Contains(classification.Declarator))
                {
                    concreteSpans.Add((
                        classification.Site.FilePath,
                        classification.Site.SpanStart,
                        classification.Site.SpanEnd));
                }
            }
        }

        return fileSites
            .Where(site => concreteSpans.Contains((
                site.FilePath, site.SpanStart, site.SpanEnd)))
            .ToHashSet();
    }

    private List<DynamicDesugarDiagnostic> EmitSiteDiagnostics(
        IReadOnlyList<DynamicSiteRecord> fileSites,
        string filePath,
        IReadOnlySet<DynamicSiteRecord>? concreteSites,
        IList<DynamicDesugarDiagnostic>? diagnosticsSink)
    {
        var diagnostics = new List<DynamicDesugarDiagnostic>();

        foreach (var site in fileSites)
        {
            // null = 该站点不发射诊断（基线错误保守台账不叠加编译器噪声）。
            var severity = DynamicDesugarSeverityPolicy.Resolve(site, _options);
            if (severity == null)
            {
                continue;
            }

            // Rewrite 模式且声明确已落地：DS001 换完成时措辞（其余消息保持审计原文）。
            var message = site.Message;
            if (site.Code == DynamicDesugarDiagnosticCodes.ProvenConcretized
                && concreteSites?.Contains(site) == true)
            {
                message = FlowMessages.Concretized(
                    site.BoundSymbolName ?? "local",
                    ProvenTypeDisplay(site),
                    $"{filePath}({site.Line + 1},{site.Column + 1})");
            }

            EmitDiagnostic(
                new DynamicDesugarDiagnostic(
                    site.Code,
                    severity.Value,
                    message,
                    filePath,
                    site.Line + 1,
                    site.Column + 1),
                diagnostics,
                diagnosticsSink);
        }

        return diagnostics;
    }

    /// <summary>从固化消息的"concretizable to 'T'"片段提取 T 展示名（BuildInventory 时已定型）。</summary>
    private static string ProvenTypeDisplay(DynamicSiteRecord site)
    {
        var message = site.Message;
        var startMarker = "concretizable to '";
        var start = message.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return "dynamic";
        }

        start += startMarker.Length;
        var end = message.IndexOf('\'', start);
        return end > start ? message.Substring(start, end - start) : "dynamic";
    }

    private void EmitDiagnostic(
        DynamicDesugarDiagnostic diagnostic,
        List<DynamicDesugarDiagnostic> local,
        IList<DynamicDesugarDiagnostic>? sink)
    {
        local.Add(diagnostic);
        sink?.Add(diagnostic);
        if (diagnostic.Severity == DynamicDesugarSeverity.Error)
        {
            HasErrors = true;
        }
    }
}
