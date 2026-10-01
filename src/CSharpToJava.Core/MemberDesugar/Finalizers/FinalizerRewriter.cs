using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.MemberDesugar.Finalizers;

/// <summary>
/// C39 终结器声明侧 rewriter（设计 §4.2/§7.4）：
/// A 档（Policy != Remove）：只发诊断、不重建任何节点，输出树与原树引用等价
/// （Changed=false，M1 行为零变化：CS2J5200 主码 + 5202/5203/5206/5207 Info +
/// 5205/5208/5211，strict 升级 Warning）。
/// B 档（Policy=Remove）：在 ClassDeclaration/RecordDeclaration 级重建成员列表，
/// Action=Removed/ForceRemoved 的终结器默认零痕迹移除；EmitRemovalMarkerComment 时原位
/// 附 marker 注释；ExtractRemovedBody 时把原 block 语句搬进永不调用的
/// private void Cs2jRemovedFinalizer_&lt;T&gt;；每次删除写 sidecar 一条并发射 CS2J5201
/// Warning（不因 strict 降级/升级）；Refused 发 5204（strict 升 Error）成员保留，
/// 5205 critical 两档一致。
/// 访问完输出树后执行 <see cref="ReconcileResidual"/>（FN-10，纯按本文件对账，不平 → 5210）。
/// </summary>
internal sealed class FinalizerRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly Dictionary<string, FinalizerPlan> _activeByFqn;
    private readonly HashSet<string> _knownFqns;
    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private readonly FinalizerSidecarReport _sidecar;
    private readonly bool _removeMode;

    // FN-10 对账台账。键为类型 FQN（每类型至多一个终结器）：不能用节点引用身份——
    // 同一编译单元内只要有一个类型因删除重建，Visit 产出的新编译单元会让未改动类型的
    // 红节点包装整体被重物化（retained 节点也不再是原实例，且脱离原语法树，旧模型
    // 无法再做符号查询）。位置在访问期按键记录，供 reconcile 在脱离树的节点上报码。
    private readonly HashSet<string> _removedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _keptKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _exemptKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Line, int Column)> _locationsByKey =
        new(StringComparer.Ordinal);
    private int _retentionCodesEmitted;
    private int _removalCodesEmitted;

    public FinalizerRewriter(
        SemanticModel model,
        IReadOnlyList<FinalizerPlan> activePlansForFile,
        IReadOnlyList<FinalizerPlan> allPlansForFile,
        MemberDesugarOptions options,
        FinalizerSidecarReport sidecar,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _model = model;
        _activeByFqn = new Dictionary<string, FinalizerPlan>(StringComparer.Ordinal);
        foreach (var p in activePlansForFile)
            _activeByFqn.TryAdd(p.TypeFqn, p); // 每类型至多一个终结器，防御性去重
        _knownFqns = allPlansForFile.Select(p => p.TypeFqn).ToHashSet(StringComparer.Ordinal);
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;
        _sidecar = sidecar;
        _removeMode = options.Finalizers.Policy == FinalizerPolicy.Remove;
    }

    // ---- A 档：M1 路径（仅 VisitDestructorDeclaration 发诊断，树不变）----

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => _removeMode ? VisitTypeDeclaration(node) : base.VisitClassDeclaration(node);

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => _removeMode ? VisitTypeDeclaration(node) : base.VisitRecordDeclaration(node);

    public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node)
    {
        if (TryResolveFqn(node, out var typeFqn))
        {
            var plan = _activeByFqn.GetValueOrDefault(typeFqn);
            if (plan != null && IsFirstDeclarationFile(plan))
            {
                EmitForRetainedPlan(node, plan);
                _keptKeys.Add(typeFqn);
                _locationsByKey[typeFqn] = LineColumn(node);
                _retentionCodesEmitted++;
            }
            else if (plan == null && _knownFqns.Contains(typeFqn))
            {
                // 被适配器 ExcludePlans 回退的 plan：成员保留、不发 5210（框架已发 5101）。
                _keptKeys.Add(typeFqn);
                _exemptKeys.Add(typeFqn);
                _locationsByKey[typeFqn] = LineColumn(node);
            }
            else
            {
                _locationsByKey[typeFqn] = LineColumn(node);
            }
        }
        return base.VisitDestructorDeclaration(node);
    }

    // ---- B 档：类型声明级成员列表重建 ----

    private SyntaxNode? VisitTypeDeclaration(TypeDeclarationSyntax node)
    {
        var newMembers = new List<MemberDeclarationSyntax>();
        var pendingMarkers = new List<(int Position, SyntaxTrivia Comment, SyntaxTrivia NewLine)>();
        var changed = false;

        foreach (var member in node.Members)
        {
            if (member is DestructorDeclarationSyntax destructor)
            {
                var plan = TryResolveFqn(destructor, out var typeFqn)
                    ? _activeByFqn.GetValueOrDefault(typeFqn)
                    : null;

                if (plan != null
                    && plan.Action is FinalizerAction.Removed or FinalizerAction.ForceRemoved
                    && IsFirstDeclarationFile(plan))
                {
                    var entry = EmitForRemoval(destructor, plan);
                    _removedKeys.Add(typeFqn);
                    _locationsByKey[typeFqn] = LineColumn(destructor);
                    _removalCodesEmitted++;
                    changed = true;

                    if (_options.Finalizers.ExtractRemovedBody)
                        newMembers.Add(BuildExtractedMethod(destructor, plan));

                    if (_options.Finalizers.EmitRemovalMarkerComment)
                    {
                        pendingMarkers.Add((newMembers.Count,
                            SyntaxFactory.Comment(FinalizerDiagnostics.RemovalMarker(
                                plan.TypeFqn, plan.BodyKind, _sidecar.DisplayPath, entry.Index)),
                            SyntaxFactory.EndOfLine("\n")));
                    }
                    continue; // 默认零痕迹：不带原位注释直接移除
                }

                if (plan != null && IsFirstDeclarationFile(plan))
                {
                    EmitForRetainedPlan(destructor, plan);
                    _keptKeys.Add(typeFqn);
                    _locationsByKey[typeFqn] = LineColumn(destructor);
                    _retentionCodesEmitted++;
                }
                else if (plan == null && typeFqn != null && _knownFqns.Contains(typeFqn))
                {
                    _keptKeys.Add(typeFqn);
                    _exemptKeys.Add(typeFqn);
                    _locationsByKey[typeFqn] = LineColumn(destructor);
                }
                else if (typeFqn != null)
                {
                    // 有 plan 但非首选声明文件（防御），或绑定得到 FQN 但无 plan 且
                    // 不在 known 集合：原样保留；位置记账供 reconcile 发 5210。
                    _locationsByKey[typeFqn] = LineColumn(destructor);
                }
                // typeFqn==null（符号完全绑定不到）：节点照原样保留，reconcile 用纯
                // 语法键对账并自行定位。
                newMembers.Add(destructor);
                continue;
            }

            var visited = (MemberDeclarationSyntax?)Visit(member);
            if (visited != null)
            {
                if (!ReferenceEquals(visited, member)) changed = true;
                newMembers.Add(visited);
            }
        }

        if (!changed) return node;

        // marker 附到其后首个保留成员的 leading trivia；之后无成员则附到闭合大括号前。
        for (var i = pendingMarkers.Count - 1; i >= 0; i--)
        {
            var (position, comment, newLine) = pendingMarkers[i];
            if (position < newMembers.Count)
            {
                var target = newMembers[position];
                newMembers[position] = target.WithLeadingTrivia(
                    target.GetLeadingTrivia().InsertRange(
                        0, new SyntaxTriviaList(comment, newLine)));
            }
        }

        var rebuilt = node.WithMembers(SyntaxFactory.List(newMembers));
        var trailingMarkers = pendingMarkers.Where(m => m.Position >= newMembers.Count).ToList();
        if (trailingMarkers.Count > 0)
        {
            var closeLeading = rebuilt.CloseBraceToken.LeadingTrivia;
            foreach (var (_, comment, newLine) in trailingMarkers)
                closeLeading = closeLeading.Add(comment).Add(newLine);
            rebuilt = rebuilt.WithCloseBraceToken(
                rebuilt.CloseBraceToken.WithLeadingTrivia(closeLeading));
        }
        return rebuilt;
    }

    /// <summary>FN-8：生成永不调用的 private void Cs2jRemovedFinalizer_&lt;TypeName&gt;，
    /// 方法体照抄原 block 语句；嵌套类用简单类型名（终结器每类型一个，同名不存在）。</summary>
    private static MethodDeclarationSyntax BuildExtractedMethod(
        DestructorDeclarationSyntax destructor, FinalizerPlan plan)
    {
        var statements = destructor.Body?.Statements
            ?? SyntaxFactory.List<StatementSyntax>();
        var simpleName = plan.Destructor.ContainingType?.Name ?? "Unknown";
        var ascii = new string(simpleName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        if (ascii.Length == 0 || char.IsDigit(ascii[0])) ascii = "_" + ascii;
        return SyntaxFactory.MethodDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                SyntaxFactory.Identifier("Cs2jRemovedFinalizer_" + ascii))
            .WithModifiers(SyntaxFactory.TokenList(
                SyntaxFactory.Token(SyntaxKind.PrivateKeyword)))
            .WithBody(SyntaxFactory.Block(statements))
            .WithLeadingTrivia(destructor.GetLeadingTrivia())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
    }

    // ---- 诊断发射（M1 全量保留：统计 + 主码 + 5211 + Info 码）----

    private void EmitForRetainedPlan(DestructorDeclarationSyntax node, FinalizerPlan plan)
    {
        EmitAnalysisStatistics(plan);

        var (line, column) = LineColumn(node);

        // ---- 主报码（每个保留终结器恰好一条）----
        if (plan.IsCriticalFinalizer
            || (plan.Action == FinalizerAction.Refused
                && plan.RefusalCode == MemberDesugarDiagnosticCode.FinalizerCriticalBlocked))
        {
            // CS2J5205：A/B 两档一致；force 不可解除。
            Emit(MemberDesugarSeverity.Error, MemberDesugarDiagnosticCode.FinalizerCriticalBlocked,
                FinalizerDiagnostics.CriticalBlocked(plan.TypeFqn), line, column);
            _stats.FinalizerCriticalBlocked++;
            Bucket(MemberDesugarDiagnosticCode.FinalizerCriticalBlocked);
        }
        else if (plan.Action == FinalizerAction.Refused
            && plan.RefusalCode == MemberDesugarDiagnosticCode.FinalizerRemoveRefused)
        {
            // CS2J5204：B 档保守拒绝（Warning，strict 升 Error），成员保留。
            Emit(_options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                MemberDesugarDiagnosticCode.FinalizerRemoveRefused,
                FinalizerDiagnostics.RemoveRefused(
                    plan.TypeFqn, plan.BodyKind, plan.Pairing,
                    plan.HasResurrectionSite, plan.BaseInfoDegraded),
                line, column);
            _stats.FinalizersRefused++;
            Bucket(MemberDesugarDiagnosticCode.FinalizerRemoveRefused);
        }
        else if (_options.Finalizers.Policy == FinalizerPolicy.CleanerGuide)
        {
            Emit(MemberDesugarSeverity.Error, MemberDesugarDiagnosticCode.FinalizerCleanerGuideUnsupported,
                FinalizerDiagnostics.CleanerGuideUnsupported(plan.TypeFqn), line, column);
        }
        else
        {
            // CS2J5200：出厂 Warning，strict 升 Error（M1 不翻 Error，设计 §2 决策 2）。
            Emit(_options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                MemberDesugarDiagnosticCode.FinalizerRejected,
                FinalizerDiagnostics.Rejected(plan.TypeFqn, plan.BodyKind, plan.Pairing),
                line, column);
        }
        _stats.FinalizersReported++;

        EmitAuxiliaryDiagnostics(node, plan, line, column);
    }

    private FinalizerSidecarEntry EmitForRemoval(DestructorDeclarationSyntax node, FinalizerPlan plan)
    {
        EmitAnalysisStatistics(plan);

        var (line, column) = LineColumn(node);
        var forced = plan.Action == FinalizerAction.ForceRemoved;
        var entry = _sidecar.Add(
            plan.TypeFqn, _filePath, line, column,
            plan.BodyKind, plan.Pairing, plan.HasBaseFinalizer, plan.BaseFinalizerFqn,
            forced, plan.BodyText);

        if (_sidecar.Path != null) _stats.FinalizerSidecarPath = _sidecar.Path;
        _stats.FinalizersRemoved++;
        if (forced) _stats.FinalizersForceRemoved++;
        _stats.FinalizerSidecarEntries++;

        // CS2J5201：Warning 恒定（不因 strict 升 Error，亦不降 Info）。
        Emit(MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.FinalizerRemoved,
            FinalizerDiagnostics.Removed(
                plan.TypeFqn, plan.BodyKind, plan.Pairing,
                forced, _sidecar.DisplayPath, entry.Index, entry.BodySha256),
            line, column);

        EmitAuxiliaryDiagnostics(node, plan, line, column);
        return entry;
    }

    private void EmitAnalysisStatistics(FinalizerPlan plan)
    {
        switch (plan.BodyKind)
        {
            case FinalizerBodyKind.Empty: _stats.FinalizerEmpty++; break;
            case FinalizerBodyKind.DisposePattern: _stats.FinalizerDisposePattern++; break;
            case FinalizerBodyKind.SafeHandleOnly: _stats.FinalizerSafeHandleOnly++; break;
            case FinalizerBodyKind.NativeRelease: _stats.FinalizerNativeRelease++; break;
            case FinalizerBodyKind.Extern: _stats.FinalizerExtern++; break;
            default: _stats.FinalizerOther++; break;
        }
        if (plan.HasResurrectionSite) _stats.FinalizerResurrectionSites += plan.ReRegisterSiteCount;
        _stats.FinalizerSuppressFinalizeSites += plan.SuppressFinalizeSiteCount;
        if (plan.HasBaseFinalizer) _stats.FinalizerChainsWithBaseFinalizer++;
        _stats.FinalizersDetected++;
    }

    private void EmitAuxiliaryDiagnostics(
        DestructorDeclarationSyntax node, FinalizerPlan plan, int line, int column)
    {
        if (plan.BaseInfoDegraded)
            Emit(MemberDesugarSeverity.Warning, MemberDesugarDiagnosticCode.FinalizerSingleFileDegraded,
                FinalizerDiagnostics.SingleFileDegraded(plan.TypeFqn), line, column);

        if (plan.BodyKind == FinalizerBodyKind.DisposePattern
            && plan.Pairing == SuppressFinalizePairing.Paired)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerDisposePattern,
                FinalizerDiagnostics.DisposePattern(plan.TypeFqn), line, column);
        }
        if (plan.BodyKind is FinalizerBodyKind.Empty or FinalizerBodyKind.SafeHandleOnly)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerRedundant,
                FinalizerDiagnostics.Redundant(plan.TypeFqn, plan.BodyKind), line, column);
        }
        if (plan.SuppressFinalizeSiteCount > 0 || plan.ReRegisterSiteCount > 0
            || plan.WaitForPendingFinalizersSiteCount > 0)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerGcLifecycleSite,
                FinalizerDiagnostics.GcLifecycleSites(
                    plan.TypeFqn, plan.SuppressFinalizeSiteCount, plan.ReRegisterSiteCount,
                    plan.WaitForPendingFinalizersSiteCount),
                line, column);
        }
        if (plan.HasBaseFinalizer)
        {
            Emit(MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.FinalizerBaseChain,
                FinalizerDiagnostics.BaseChain(plan.TypeFqn, plan.BaseFinalizerFqn, plan.BaseInfoDegraded),
                line, column);
        }
    }

    // ---- FN-10 residual 闸门（输出树访问完成后由 preprocessor 调用，纯按本文件对账）----

    internal void ReconcileResidual(CompilationUnitSyntax outputRoot)
    {
        // 输出树按纯语法 FQN 键建账（每类型至多一个终结器）。输出节点可能已脱离原语法树，
        // 此处禁止再做语义查询——全部决策在访问期已按同一 FQN 键记录。
        var outputByKey = new Dictionary<string, DestructorDeclarationSyntax>(StringComparer.Ordinal);
        foreach (var destructor in outputRoot.DescendantNodes().OfType<DestructorDeclarationSyntax>())
            outputByKey.TryAdd(SynthesizeTypeFqn(destructor), destructor);

        var nodeMismatch = false;

        // 应删未删：plan 决策 Removed/ForceRemoved 的类型在输出树中仍有终结器。
        foreach (var removedKey in _removedKeys)
        {
            if (outputByKey.TryGetValue(removedKey, out var residual))
            {
                EmitResidual(residual, removedKey,
                    "finalizer was decided for removal but a destructor declaration still "
                    + $"exists in the output for [{removedKey}]");
                nodeMismatch = true;
            }
        }

        // 有树无账：输出中的终结器不在保留/豁免台账（含未知节点；排除集合的 plan 属豁免）。
        foreach (var (key, residual) in outputByKey)
        {
            if (_keptKeys.Contains(key) || _removedKeys.Contains(key)) continue;
            EmitResidual(residual, key,
                "destructor declaration in the output has no registered finalizer plan decision "
                + $"for [{key}]");
            nodeMismatch = true;
        }

        // 数量双向对账（节点异常已逐条发码，这里仅兜节点身份之外的计数漂移，避免同因双报）。
        var retainedOutputs = outputByKey.Count - outputByKey.Keys.Count(_exemptKeys.Contains);
        if (!nodeMismatch
            && (retainedOutputs != _retentionCodesEmitted
                || _removedKeys.Count != _removalCodesEmitted))
        {
            EmitResidual(null, null,
                "finalizer residual accounting mismatch: "
                + $"{retainedOutputs} retained destructor(s) vs {_retentionCodesEmitted} retention "
                + $"diagnostic(s), {_removedKeys.Count} removed plan(s) vs {_removalCodesEmitted} "
                + "CS2J5201 diagnostic(s)");
        }
    }

    /// <summary>纯语法构造含入类型 FQN（命名空间.外层类型.内层类型，泛型补元数据 arity 后缀）。
    /// 与符号 ToDisplayString 在终结器场景（任意嵌套/泛型类）同形；不依赖语义模型，
    /// 可安全用于脱离语法树的输出节点。</summary>
    private static string SynthesizeTypeFqn(DestructorDeclarationSyntax node)
    {
        var types = new List<string>();
        var namespaces = new List<string>();
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case TypeDeclarationSyntax type:
                    var arity = type.TypeParameterList?.Parameters.Count ?? 0;
                    types.Add(arity > 0 ? type.Identifier.Text + "`" + arity : type.Identifier.Text);
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    // FileScopedNamespaceDeclarationSyntax 亦派生自 BaseNamespaceDeclarationSyntax。
                    namespaces.Add(ns.Name.ToString());
                    break;
            }
        }
        types.Reverse();
        namespaces.Reverse();
        return string.Join(".", namespaces.Concat(types));
    }

    private bool TryResolveFqn(DestructorDeclarationSyntax node, out string? typeFqn)
    {
        if (node.SyntaxTree == _model.SyntaxTree
            && _model.GetDeclaredSymbol(node) is { } method
            && method.MethodKind == MethodKind.Destructor
            && method.ContainingType != null)
        {
            typeFqn = method.ContainingType.ToDisplayString();
            return true;
        }
        typeFqn = null;
        return false;
    }

    private void EmitResidual(DestructorDeclarationSyntax? node, string? key, string message)
    {
        var line = 0;
        var column = 0;
        if (key != null && _locationsByKey.TryGetValue(key, out var located))
            (line, column) = located;
        Emit(_options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
            MemberDesugarDiagnosticCode.FinalizerResidual, message, line, column);
        _stats.FinalizerResidual++;
        Bucket(MemberDesugarDiagnosticCode.FinalizerResidual);
    }

    private void Bucket(MemberDesugarDiagnosticCode code)
    {
        _stats.SkippedByReason.TryGetValue(code, out var count);
        _stats.SkippedByReason[code] = count + 1;
    }

    private void Emit(
        MemberDesugarSeverity severity, MemberDesugarDiagnosticCode code,
        string message, int line, int column)
        => _diagnostics.Add(new MemberDesugarDiagnostic(
            severity, code, message, _filePath, line, column, null));

    private (int Line, int Column) LineColumn(DestructorDeclarationSyntax node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private bool IsFirstDeclarationFile(FinalizerPlan plan)
    {
        var first = plan.Symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return first != null
            && string.Equals(first.SourceTree?.FilePath, _filePath, StringComparison.OrdinalIgnoreCase);
    }
}
