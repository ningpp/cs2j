using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.MemberDesugar;

/// <summary>
/// member-desugar 预处理器入口（C11 锚点）：三阶段分析→计划→重写，
/// 计划级排除支持 CLI 回退循环（设计 §4.2/§4.3）。与 RR/JK 同形：
/// 仅构造器与 <see cref="RewriteFile"/> 为 public，其余 internal。
/// </summary>
public sealed partial class MemberDesugarPreprocessor
{
    private readonly MemberDesugarOptions _options;
    private IReadOnlyList<MemberDesugarPlan>? _plans;
    private Dictionary<string, int>? _metadataSitesByFile;
    private Dictionary<string, Signatures.SignatureStatistics>? _signatureAuditByFile;
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

    /// <summary>C39 FN-8：本 run 的 sidecar（懒创建；适配器每轮迭代新建 preprocessor，
    /// 首次实际删除时重置旧文件）。定位不到 .cs2j-member-desugar-src 段时为内存模式。</summary>
    private Finalizers.FinalizerSidecarReport? _finalizerSidecar;

    /// <summary>每线程复用一个 <see cref="AdhocWorkspace"/>：格式化服务的宿主由 workspace 提供，
    /// 复用可省掉逐文件新建 workspace 的开销（画像 2026-10-17：845 次 ≈ 8.5s CPU）。
    /// 线程内独占使用，故无需考虑跨线程竞争；不 Dispose（线程级缓存，进程退出自然回收）。</summary>
    [ThreadStatic]
    private static AdhocWorkspace? _threadWorkspace;

    private static AdhocWorkspace SharedWorkspace() => _threadWorkspace ??= new AdhocWorkspace();

    public MemberDesugarPreprocessor(MemberDesugarOptions? options = null)
        => _options = options ?? new MemberDesugarOptions();

    public MemberDesugarFileResult RewriteFile(
        string sourceCode,
        SemanticModel semanticModel,
        MemberDesugarStatistics statistics,
        List<MemberDesugarDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(sourceCode);
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(diagnostics);

        statistics.FilesScanned++;
        var root = (CompilationUnitSyntax)semanticModel.SyntaxTree.GetRoot();
        var filePath = semanticModel.SyntaxTree.FilePath;

        EnsureBuilt((CSharpCompilation)semanticModel.Compilation);

        // 决策 9：元数据-only 站点在分析期按文件计数，此处并入统计（无 plan 文件也计）
        if (_metadataSitesByFile != null
            && _metadataSitesByFile.TryGetValue(filePath, out var metadataSites))
            statistics.MetadataOperatorSitesUntouched += metadataSites;

        // C46：签名审计计数并入族统计 + 每文件一条 CS2J5190 Info（仅 ReportInfo）。
        // 必须早于本方法末尾的 FilesSkippedClean 提前返回——干净文件（M1 无 plan）也要计入。
        if (_signatureAuditByFile != null
            && _signatureAuditByFile.TryGetValue(filePath, out var signatureAudit))
        {
            signatureAudit.ApplyTo(statistics);
            if (_options.Signatures.ReportInfo && signatureAudit.HasAny)
                diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Info,
                    MemberDesugarDiagnosticCode.SignaturePlanCreated,
                    Signatures.SignatureDiagnostics.AuditSummary(signatureAudit),
                    filePath, 0, 0, null));
        }

        var active = _plans!
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        foreach (var p in active.Where(p => p.Outcome != MemberDesugarOutcome.Planned))
            EmitSkipDiagnostics(p, filePath, statistics, diagnostics);

        var propertyPlans = active.OfType<Properties.PropertyPlan>().ToList();
        var eventPlans = active.OfType<Events.EventPlan>().ToList();
        var indexerPlans = active.OfType<Indexers.IndexerPlan>().ToList();
        var operatorPlans = active.OfType<Operators.OperatorPlan>().ToList();
        var finalizerPlans = active.OfType<Finalizers.FinalizerPlan>().ToList();
        var signaturePlans = active.OfType<Signatures.ExplicitInterface.SiPlan>().ToList();
        var hidingPlans = active.OfType<Signatures.Hiding.NhPlan>().ToList();
        var covariantPlans = active.OfType<Signatures.Covariant.CrPlan>().ToList();
        // C27：Records 全部 plan 恒为 Planned（跳过诊断由重写阶段按站点自发射，框架
        // EmitSkipDiagnostics 无法表达逐站点位置），不能按 Outcome 过滤。
        var recordPlans = active.OfType<Records.RecordClonePlan>().ToList();
        var plannedProperties = propertyPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedEvents = eventPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedIndexers = indexerPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedOperators = operatorPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedSignatures = signaturePlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedHiding = hidingPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedCovariant = covariantPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        // C46 SI-7：本文件全部签名 plan（含被回退/跳过项）传给重写器作 residual 归属集合。
        var allSignaturePlansForFile = _plans!
            .OfType<Signatures.ExplicitInterface.SiPlan>()
            .Where(p => p.TouchedFiles.Contains(filePath))
            .ToList();
        // C46 CR-4：本文件全部协变 plan（含 Retained/Skipped）作 residual 归属集合。
        var allCovariantPlansForFile = _plans!
            .OfType<Signatures.Covariant.CrPlan>()
            .Where(p => p.TouchedFiles.Contains(filePath))
            .ToList();
        // Info 级白名单（inc/dec、门控 &/|）在重写期计数：文件含 operator 声明时
        // 即使无任何 plan 也过一遍 operator rewriter（树不变 → 计数后按 clean 跳过）。
        var hasOperatorDecls = root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Any();
        // C39：含终结器声明的文件绝不能被 clean 跳过，否则 A 档零诊断（FN-5 信号真空）。
        var hasDestructorDecls = root.DescendantNodes().OfType<DestructorDeclarationSyntax>().Any();
        // C27：本文件含 Records plan 触点（声明部件或 with 站点，含跳过族站点）时绝不
        // clean 早退——逐站点 52xx 诊断与 WR-8 residual 对账都在重写阶段发射；开关关闭
        // 时分析层零 plan，此处恒 false（默认关零行为铁律）。
        var hasRecordClaim = recordPlans.Count > 0;
        if (plannedProperties.Count == 0 && plannedEvents.Count == 0
            && plannedIndexers.Count == 0 && plannedOperators.Count == 0
            && plannedSignatures.Count == 0 && plannedHiding.Count == 0
            && plannedCovariant.Count == 0 && !hasOperatorDecls
            && !hasDestructorDecls && !hasRecordClaim)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOperators.Count(p => IsFirstDeclarationFile(p, filePath));
        // C46 NH-3：保留同名桥接的 plan 不改树（无重写器动作），计数在此并入。
        statistics.SignatureHideBridged += plannedHiding.Count(p =>
            p is Signatures.Hiding.NhPlan { Bridged: true } && IsFirstDeclarationFile(p, filePath));

        // 四级 dispatch：属性->事件(重解析)->运算符(重解析)->索引器
        // 属性改写器在原始树上运行；事件/运算符改写器经替换树运行（属性产物是红色节点，
        // 语义查询必须绑定自有模型）；索引器最后。事件 plan 归属键使用「含类型::事件名」显示串，
        // 运算符 plan 归属键使用「含类型::op 名(参数):返回」（设计 §4.6）。
        var workingRoot = root;
        // 随重写链推进的活动模型：finalizer B 档删除会重建编译单元（同文件内未改动类型的
        // 红节点包装也被重物化并脱离原树），其后任何语义查询都必须绑定重解析后的新鲜模型。
        var activeModel = semanticModel;

        // C27 record `with`：阶段序在 finalizer/property 之前。plan.Sites 持有原始
        // WithExpressionSyntax 引用，声明注入与站点改写必须同遍在原始树上完成；且
        // PropertyRule 的 5124 让位钩使被认领属性的 PropertyPlan 正常生成，整个 with
        // 节点必须在 property 阶段之前消除，避免初始化器被当对象初始化器二次处理。
        if (recordPlans.Count > 0)
        {
            statistics.RecordPlans += recordPlans.Count(p =>
                !p.IsOrphan && IsFirstDeclarationFile(p, filePath));
            statistics.RecordClassPlans += recordPlans.Count(p =>
                !p.IsOrphan
                && p.RecordType is { IsRecord: true, TypeKind: TypeKind.Class }
                && IsFirstDeclarationFile(p, filePath));

            var allRecordPlansForFile = _plans!
                .OfType<Records.RecordClonePlan>()
                .Where(p => p.TouchedFiles.Contains(filePath))
                .ToList();
            var recordRewriter = new Records.WithExpressionRewriter(
                recordPlans, allRecordPlansForFile,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)recordRewriter.Visit(workingRoot)!;
            // WR-8 residual 对账必须在重解析前：跳过标注挂在保留不动的 with 节点上，
            // 重解析会丢失标注。已重写站点已离树，未登记残留发 5230。
            recordRewriter.ReconcileResidual(workingRoot);

            // Changed=false 但根已因跳过标注重挂时也要重解析：后续 finalizer/property
            // 阶段的语义查询不能落到不在原语法树中的红色节点上。
            if (recordRewriter.Changed || !ReferenceEquals(workingRoot, root))
            {
                var recordIntermediate = FormatAndValidate(workingRoot);
                if (recordIntermediate == null)
                {
                    statistics.FilesSkippedClean++;
                    diagnostics.Add(new MemberDesugarDiagnostic(
                        MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                        "member-desugar rewrite failed local parse validation; the original file was " +
                        "preserved pending whole-tree validation.", filePath, 0, 0, null));
                    return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
                }
                var (replacementModel, replacementRoot) =
                    ParseReplacement(activeModel, recordIntermediate);
                activeModel = replacementModel;
                workingRoot = replacementRoot;
            }
        }

        // C39（M1）：在属性阶段之前，基于当前活动 root/模型先跑 finalizer rewriter。
        // M1 只发诊断、树不变（Changed=false）；M2 将在此顺序上删除成员。
        if (hasDestructorDecls)
        {
            // 懒定位中间树根（首个含终结器文件的路径决定 sidecar 落点；无中间树段 → 内存模式）。
            _finalizerSidecar ??= Finalizers.FinalizerSidecarReport.CreateFor(filePath);
            // FN-10：全量 finalizer plans（含 ExcludePlans 回退项）传入 known 集合，
            // 被排除成员保留且不计 residual。
            var allFinalizerPlansForFile = _plans!
                .OfType<Finalizers.FinalizerPlan>()
                .Where(p => p.TouchedFiles.Contains(filePath))
                .ToList();
            var finalizerRewriter = new Finalizers.FinalizerRewriter(
                activeModel, finalizerPlans, allFinalizerPlansForFile,
                _options, _finalizerSidecar, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)finalizerRewriter.Visit(workingRoot)!;
            finalizerRewriter.ReconcileResidual(workingRoot);

            // B 档删除后树已重建：格式化 + 重解析换取可绑定的活动模型（与 operator/indexer
            // 阶段同形）。原 finalizer 输出含成员删除处的紧凑 trivia，格式化同时规整间距。
            if (!ReferenceEquals(workingRoot, root))
            {
                var finalizerIntermediate = FormatAndValidate(workingRoot);
                if (finalizerIntermediate == null)
                {
                    statistics.FilesSkippedClean++;
                    diagnostics.Add(new MemberDesugarDiagnostic(
                        MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                        "member-desugar rewrite failed local parse validation; the original file was " +
                        "preserved pending whole-tree validation.", filePath, 0, 0, null));
                    return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
                }
                var (replacementModel, replacementRoot) =
                    ParseReplacement(activeModel, finalizerIntermediate);
                activeModel = replacementModel;
                workingRoot = replacementRoot;
            }
        }
        if (plannedProperties.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                activeModel, plannedProperties, propertyPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)rewriter.Visit(workingRoot)!;
        }
        if (plannedEvents.Count > 0)
        {
            // 属性改写产物的合成 token 间距由最终 Formatter 修复；重解析前必须先格式化，
            // 否则无空格相邻 token（如 private int / void add_E）会融合成非法标识符。
            workingRoot = (CompilationUnitSyntax)Formatter.Format(workingRoot, SharedWorkspace());
            var eventTree = CSharpSyntaxTree.ParseText(
                workingRoot.ToFullString(), (CSharpParseOptions)activeModel.SyntaxTree.Options, path: filePath);
            var eventCompilation = ((CSharpCompilation)activeModel.Compilation)
                .ReplaceSyntaxTree(activeModel.SyntaxTree, eventTree);
            var eventModel = eventCompilation.GetSemanticModel(eventTree);
            var eventRewriter = new Events.EventUseSiteRewriter(
                eventModel, plannedEvents, eventPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)eventRewriter.Visit(eventTree.GetRoot())!;
            activeModel = eventModel;
        }
        if (plannedOperators.Count > 0 || hasOperatorDecls)
        {
            // 事件/属性产物含无分隔 trivia 的合成节点（文本级 publicint/returnp），必须先经
            // 格式化+本地校验取干净文本，再解析回编译换取可绑定的新鲜模型。
            var intermediate = FormatAndValidate(workingRoot);
            if (intermediate == null)
            {
                statistics.FilesSkippedClean++;
                diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                    "member-desugar rewrite failed local parse validation; the original file was " +
                    "preserved pending whole-tree validation.", filePath, 0, 0, null));
                return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
            }
            SemanticModel operatorModel;
            if (ReferenceEquals(workingRoot, root))
            {
                operatorModel = semanticModel;
            }
            else
            {
                var (replacementModel, replacementRoot) = ParseReplacement(semanticModel, intermediate);
                operatorModel = replacementModel;
                workingRoot = replacementRoot;
            }
            var opRewriter = new Operators.OperatorUseSiteRewriter(
                operatorModel, plannedOperators, operatorPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)opRewriter.Visit(workingRoot)!;
        }
        if (plannedIndexers.Count > 0)
        {
            // 与 operator 阶段同形：前置 finalizer/property/event 阶段可能已重建树
            // （C39 B 档删除成员或属性使用点改写都会产生红色节点；事件阶段还会重解析），
            // 索引器在使用点文件（plan TouchedFiles 含跨文件使用点）上用旧模型做
            // GetSymbolInfo 会抛「语法节点不在语法树中」。树已变则先格式化+重解析
            // 换新鲜模型，再跑索引器重写。
            SemanticModel indexerModel;
            if (ReferenceEquals(workingRoot, root))
            {
                indexerModel = semanticModel;
            }
            else
            {
                var indexerIntermediate = FormatAndValidate(workingRoot);
                if (indexerIntermediate == null)
                {
                    statistics.FilesSkippedClean++;
                    diagnostics.Add(new MemberDesugarDiagnostic(
                        MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                        "member-desugar rewrite failed local parse validation; the original file was " +
                        "preserved pending whole-tree validation.", filePath, 0, 0, null));
                    return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
                }
                var (replacementModel, replacementRoot) =
                    ParseReplacement(semanticModel, indexerIntermediate);
                indexerModel = replacementModel;
                workingRoot = replacementRoot;
            }

            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                indexerModel, plannedIndexers, indexerPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)indexerRewriter.Visit(workingRoot)!;
        }
        if (plannedSignatures.Count > 0 || plannedHiding.Count > 0 || plannedCovariant.Count > 0)
        {
            // C46 签名分相晚于索引器：前置阶段可能已重建树（红色节点/重解析），
            // 与 operator/indexer 同形——树已变先格式化+重解析换可绑定的新鲜模型。
            var (sigRoot, sigModel) = ResolveSignatureStage(workingRoot);
            if (sigRoot == null) return RollbackSignatureStage();

            // SI（Order 40）先于 NH（Order 50）先于 CR（Order 60）。
            if (plannedSignatures.Count > 0)
            {
                var signatureRewriter = new Signatures.ExplicitInterface.ExplicitInterfaceRewriter(
                    sigModel!, plannedSignatures, allSignaturePlansForFile,
                    _options, statistics, diagnostics, filePath);
                sigRoot = (CompilationUnitSyntax)signatureRewriter.Visit(sigRoot)!;
            }

            if (plannedHiding.Count > 0)
            {
                // SI 已改树则重新取新鲜模型（NH 绑定不得使用脱离语法树的红节点）。
                var (hideRoot, hideModel) = ResolveSignatureStage(sigRoot);
                if (hideRoot == null) return RollbackSignatureStage();
                var hidingRewriter = new Signatures.Hiding.HidingRewriter(
                    hideModel!, plannedHiding, _options, statistics, diagnostics, filePath);
                sigRoot = (CompilationUnitSyntax)hidingRewriter.Visit(hideRoot)!;
            }

            if (plannedCovariant.Count > 0)
            {
                // CR 桥接晚于 NH：SI/NH 已改树则重新取新鲜模型（CR 绑定同样不得使用红节点）。
                var (covRoot, covModel) = ResolveSignatureStage(sigRoot);
                if (covRoot == null) return RollbackSignatureStage();
                var covariantRewriter = new Signatures.Covariant.CovariantBridgeRewriter(
                    covModel!, plannedCovariant, allCovariantPlansForFile,
                    _options, statistics, diagnostics, filePath);
                sigRoot = covariantRewriter.Rewrite(covRoot);
            }
            workingRoot = sigRoot;

            // 重解析前的树变更：树已变先格式化+重解析换可绑定的新鲜模型。
            (CompilationUnitSyntax? Root, SemanticModel? Model) ResolveSignatureStage(CompilationUnitSyntax working)
            {
                if (ReferenceEquals(working, root)) return (working, semanticModel);
                var intermediate = FormatAndValidate(working);
                if (intermediate == null) return (null, null);
                var (model, replacementRoot) = ParseReplacement(semanticModel, intermediate);
                return (replacementRoot, model);
            }

            MemberDesugarFileResult RollbackSignatureStage()
            {
                statistics.FilesSkippedClean++;
                diagnostics.Add(new MemberDesugarDiagnostic(
                    MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                    "member-desugar rewrite failed local parse validation; the original file was " +
                    "preserved pending whole-tree validation.", filePath, 0, 0, null));
                return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
            }
        }
        if (workingRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOperators.Count(p => IsFirstDeclarationFile(p, filePath));

        var outputCode = FormatAndValidate(workingRoot);
        if (outputCode == null)
        {
            statistics.FilesSkippedClean++;
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.PlanRolledBack,
                "member-desugar rewrite failed local parse validation; the original file was " +
                "preserved pending whole-tree validation.", filePath, 0, 0, null));
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }

        statistics.FilesTransformed++;
        return new MemberDesugarFileResult { OutputCode = outputCode, Changed = true };
    }

    internal void BindCompilation(Compilation compilation)
        => EnsureBuilt((CSharpCompilation)compilation);

    /// <summary>把中间产物文本解析回编译，产出可绑定新节点的
    /// 新鲜模型与其附着根（operator 重写器用；设计 §4.6 键桥接）。仅混族文件触发。</summary>
    private static (SemanticModel Model, CompilationUnitSyntax Root) ParseReplacement(
        SemanticModel original, string intermediateText)
    {
        var compilation = (CSharpCompilation)original.Compilation;
        var newTree = CSharpSyntaxTree.ParseText(
            intermediateText,
            (CSharpParseOptions)original.SyntaxTree.Options,
            original.SyntaxTree.FilePath);
        var newCompilation = compilation.ReplaceSyntaxTree(original.SyntaxTree, newTree);
        return (newCompilation.GetSemanticModel(newTree),
            (CompilationUnitSyntax)newTree.GetRoot());
    }

    internal IReadOnlyList<MemberDesugarPlan> Plans
        => _plans ?? throw new InvalidOperationException(
            "Preprocessor has not analyzed a compilation yet.");

    /// <summary>C39 FN-8：本 run 内存 sidecar 条目（无删除时为空；供单测断言）。</summary>
    internal IReadOnlyList<Finalizers.FinalizerSidecarEntry> FinalizerSidecarEntries
        => _finalizerSidecar?.Entries
           ?? (IReadOnlyList<Finalizers.FinalizerSidecarEntry>)
               Array.Empty<Finalizers.FinalizerSidecarEntry>();

    /// <summary>C39 FN-8：sidecar 文件实际落盘路径（内存模式/无删除时为 null）。</summary>
    internal string? FinalizerSidecarPath => _finalizerSidecar?.Path;

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    /// <summary>
    /// 整树回退循环每轮迭代开始时调用：重置 run 级 sidecar 状态，镜像历史"每轮新建
    /// preprocessor"语义——否则跨轮复用同一实例时，上一轮已登记的删除条目会让本轮
    /// 重删同一终结器时重复登记（sidecar 条目翻倍，C39 FN-8）。
    /// </summary>
    internal void BeginIteration() => _finalizerSidecar = null;

    /// <summary>
    /// 归属（与 JK 同款键：位置 → (类型名, 成员名) → plan owner 集合）。返回该位置的
    /// **全部** owner（含已排除者，按 Id 序）；调用方取其中【活动】者作本轮回退集——
    /// 活动集为空即本轮无可回退的活动 plan 能消除该错误（镜像碰撞 / 跨文件使用点）。
    ///
    /// 不要求锚点文件本轮被改写：plan 权属已含 TouchedFiles.Contains(file) 门；接口实现
    /// 与继承联动文件常因无实际站点而逐字节不变（Changed=false），却因对侧成员的改写产生
    /// CS0115/CS0539/CS0737 等错误，此时仍须把该 plan 纳入回退（画像 2026-10-17）。
    /// 同一位置常有多个 owner（如显式接口实现属性同时被属性 plan 与签名 plan 认领、
    /// 基类与派生各一个 plan），它们必须整组回退才能恢复一致——故返回全体而非要求唯一。
    /// </summary>
    internal bool CollectOwners(
        Diagnostic diagnostic,
        out List<string> owners)
    {
        owners = new List<string>();
        if (_plans == null || diagnostic.Location.SourceTree is not { } tree) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberNames = new List<string?>
        {
            node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
                .FirstOrDefault()?.GetNameString()
        };

        // CS0082 报点常落在 get/set/add/remove 访问器槽位（GetNameString 对
        // AccessorDeclaration 返回 null）；镜像碰撞场景下锚点文件本轮保留原属性，
        // 必须补登记所属属性/事件的声明名，OwnsTouch 内部再匹配 get_X/set_X 槽位名。
        if (node.AncestorsAndSelf().OfType<BasePropertyDeclarationSyntax>().FirstOrDefault()
            is { } propertyLike)
        {
            switch (propertyLike)
            {
                case PropertyDeclarationSyntax p:
                    memberNames.Add(p.Identifier.Text);
                    break;
                case EventDeclarationSyntax e:
                    memberNames.Add(e.Identifier.Text);
                    break;
            }
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (memberNames.Any(n => plan.OwnsTouch(tree.FilePath, typeName, n)))
                set.Add(plan.Id);
        }
        owners = set.OrderBy(id => id, StringComparer.Ordinal).ToList();
        return owners.Count > 0;
    }

    /// <summary>属性 pass 产出新树后，索引器 pass 需要绑定新节点的 SemanticModel：
    /// <summary>重解析改写文本并换树取新模型；返回新树根，后续 pass 必须访问新树
    /// 节点（SemanticModel 只能绑定自身语法树的节点）。改写产物可能缺词法空白
    /// （如 privateint，收尾格式化才补），直接重解析会把 privateint 当作类型名固化，
    /// 因此先整树格式化再解析。</summary>
    private static (SemanticModel Model, CompilationUnitSyntax Root) RebindModel(
        SemanticModel original, CompilationUnitSyntax rewritten)
    {
        string text;
        var workspace = SharedWorkspace();
        try { text = Formatter.Format(rewritten, workspace).ToFullString(); }
        catch { text = rewritten.ToFullString(); }
        var oldTree = original.SyntaxTree;
        var newTree = CSharpSyntaxTree.ParseText(
            text, (CSharpParseOptions)oldTree.Options, oldTree.FilePath);
        var newCompilation = original.Compilation.ReplaceSyntaxTree(oldTree, newTree);
        return (newCompilation.GetSemanticModel(newTree),
            (CompilationUnitSyntax)newTree.GetRoot());
    }

    /// <summary>三候选：改动区间格式化 → 整文件格式化 → 原文；重解析零错误取首个（RR/JK 同款）。</summary>
    internal static string? FormatAndValidate(CompilationUnitSyntax root)
    {
        var workspace = SharedWorkspace();

        // 候选按需惰性求值，选取结果与历史"先构造三候选再取首个可解析者"逐字相同；
        // 绝大多数文件首候选即可解析，省掉一次整树 Formatter.Format。
        List<TextSpan> spans;
        try
        {
            spans = root
                .GetAnnotatedNodes(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)
                .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                .Where(n => !n.Ancestors().Any(a =>
                    a.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)))
                .Select(n => n.Span)
                .Distinct()
                .OrderBy(s => s.Start)
                .ToList();
        }
        catch
        {
            spans = new List<TextSpan>();
        }

        // 大 span 集（生成代码常见：单文件可达 5 万 span）下 "span 限定格式化" 的成本近似按
        // span 数线性（≈0.45ms/span，画像 2026-10-17：单文件 22.7s 对整树 0.9s）。先做一次
        // 整树规范化并与原文比较：两者相同即说明整树本就是规范化形态，那么任何子集（含本
        // pass 的 span 限定）都不会产生改动 —— 直接返回原文与 span 限定路径**可证明逐字节
        // 等价**，且快约 20×；不相同则仍走下面的历史 span 路径（逐字节保留旧行为）。
        const int WholeNormalizeSpanThreshold = 1024;
        if (spans.Count >= WholeNormalizeSpanThreshold)
        {
            try
            {
                var whole = Formatter.Format(root, workspace).ToFullString();
                var raw = root.ToFullString();
                if (string.Equals(whole, raw, StringComparison.Ordinal))
                    return raw;
            }
            catch
            {
            }
        }

        if (spans.Count > 0)
        {
            try
            {
                var spanCandidate = Formatter.Format(root, spans, workspace).ToFullString();
                if (ParsesClean(spanCandidate))
                    return spanCandidate;
            }
            catch
            {
            }
        }

        try
        {
            var wholeCandidate = Formatter.Format(root, workspace).ToFullString();
            if (ParsesClean(wholeCandidate))
                return wholeCandidate;
        }
        catch
        {
        }

        var rawText = root.ToFullString();
        return ParsesClean(rawText) ? rawText : null;
    }

    /// <summary>候选文本重解析零错误（RR/JK 同款本地校验；候选筛选条件与历史一致）。</summary>
    private static bool ParsesClean(string candidate)
    {
        try
        {
            var reparsed = CSharpSyntaxTree.ParseText(
                candidate, new CSharpParseOptions(LanguageVersion.Latest));
            return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureBuilt(CSharpCompilation compilation)
    {
        if (_plans != null) return;
        var (context, plans) = MemberDesugarAnalyzer.Analyze(compilation, _options);
        _plans = plans;
        _metadataSitesByFile = context.MetadataOperatorSitesByFile;
        _signatureAuditByFile = context.SignatureAuditByFile;
    }

    private static bool IsFirstDeclarationFile(MemberDesugarPlan plan, string filePath)
    {
        var first = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return first != null
            && string.Equals(first.SourceTree?.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitSkipDiagnostics(
        MemberDesugarPlan p, string filePath,
        MemberDesugarStatistics statistics, List<MemberDesugarDiagnostic> diagnostics)
    {
        // C46 签名 plan：属性/事件专用兜底码与文案会误标（5199 兜底，级别按 SignatureOptions）。
        if (p is Signatures.ExplicitInterface.SiPlan or Signatures.Hiding.NhPlan
            or Signatures.Covariant.CrPlan)
        {
            EmitSignatureSkipDiagnostic(p, filePath, statistics, diagnostics);
            return;
        }

        if (p.Outcome == MemberDesugarOutcome.UserSkipped)
        {
            // 只在声明文件发一次（Info）。
            if (!IsFirstDeclarationFile(p, filePath)) return;
            var isEvent = p is Events.EventPlan;
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info,
                isEvent ? MemberDesugarDiagnosticCode.EventPlanCreated : MemberDesugarDiagnosticCode.PlanCreated,
                isEvent ? "event plan skipped by overrides.skip configuration"
                        : "property plan skipped by overrides.skip configuration",
                filePath, 0, 0, p.SymbolName));
            return;
        }

        // Infeasible：在首个声明文件发一次 Warning（strict 升级 Error）。
        if (!IsFirstDeclarationFile(p, filePath)) return;
        var severity = _options.Strict
            ? MemberDesugarSeverity.Error
            : MemberDesugarSeverity.Warning;
        var loc = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var line = 0;
        var col = 0;
        if (loc != null)
        {
            var span = loc.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            col = span.StartLinePosition.Character + 1;
        }
        statistics.PlansSkipped++;
        var fallbackCode = p is Events.EventPlan
            ? MemberDesugarDiagnosticCode.ResidualEvent
            : MemberDesugarDiagnosticCode.ResidualProperty;
        var code = p.FailCode ?? fallbackCode;
        statistics.SkippedByReason.TryGetValue(code, out var count);
        statistics.SkippedByReason[code] = count + 1;
        diagnostics.Add(new MemberDesugarDiagnostic(
            severity, code, p.FailReason ?? "property plan infeasible",
            filePath, line, col, p.SymbolName));
    }

    /// <summary>C46 签名 plan 的跳过诊断（UserSkipped Info / Infeasible 带码；兜底 CS2J5199），
    /// 级别按 <see cref="Signatures.SignatureOptions"/>（5198 恒 Info，5193 非阻断）。</summary>
    private void EmitSignatureSkipDiagnostic(
        MemberDesugarPlan p, string filePath,
        MemberDesugarStatistics statistics, List<MemberDesugarDiagnostic> diagnostics)
    {
        if (p.Outcome == MemberDesugarOutcome.UserSkipped)
        {
            if (!IsFirstDeclarationFile(p, filePath)) return;
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.SignaturePlanCreated,
                "signature plan skipped by overrides.skip configuration",
                filePath, 0, 0, p.SymbolName));
            return;
        }

        if (!IsFirstDeclarationFile(p, filePath)) return;
        var code = p.FailCode ?? MemberDesugarDiagnosticCode.SignatureResidual;
        // C46 NH-5/SI-7：residual 对账码（输出树残留无归属的隐藏性 new/显式接口）单独计数。
        if (code == MemberDesugarDiagnosticCode.SignatureResidual) statistics.SignatureResidual++;
        var line = 0;
        var col = 0;
        var loc = p.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc != null)
        {
            var span = loc.GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            col = span.StartLinePosition.Character + 1;
        }
        statistics.PlansSkipped++;
        statistics.SkippedByReason.TryGetValue(code, out var count);
        statistics.SkippedByReason[code] = count + 1;
        diagnostics.Add(Signatures.SignatureDiagnostics.Create(
            code, p.FailReason ?? "signature plan infeasible", _options.Signatures,
            p.SymbolName, filePath, line, col));
    }
}

internal static class MemberDesugarDeclarationNaming
{
    public static string? GetNameString(this MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        _ => null,
    };
}
