using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

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
    private readonly HashSet<string> _excludedPlanIds = new(StringComparer.Ordinal);

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

        var active = _plans!
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        foreach (var p in active.Where(p => p.Outcome != MemberDesugarOutcome.Planned))
            EmitSkipDiagnostics(p, filePath, statistics, diagnostics);

        var propertyPlans = active.OfType<Properties.PropertyPlan>().ToList();
        var eventPlans = active.OfType<Events.EventPlan>().ToList();
        var indexerPlans = active.OfType<Indexers.IndexerPlan>().ToList();
        var operatorPlans = active.OfType<Operators.OperatorPlan>().ToList();
        var plannedProperties = propertyPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedEvents = eventPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedIndexers = indexerPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedOperators = operatorPlans
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        // Info 级白名单（inc/dec、门控 &/|）在重写期计数：文件含 operator 声明时
        // 即使无任何 plan 也过一遍 operator rewriter（树不变 → 计数后按 clean 跳过）。
        var hasOperatorDecls = root.DescendantNodes().OfType<OperatorDeclarationSyntax>().Any();
        if (plannedProperties.Count == 0 && plannedEvents.Count == 0
            && plannedIndexers.Count == 0 && plannedOperators.Count == 0 && !hasOperatorDecls)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProperties.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.EventsPlanned += plannedEvents.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.OperatorsPlanned += plannedOperators.Count(p => IsFirstDeclarationFile(p, filePath));

        // 四级 dispatch：属性->事件(重解析)->运算符(重解析)->索引器
        // 属性改写器在原始树上运行；事件/运算符改写器经替换树运行（属性产物是红色节点，
        // 语义查询必须绑定自有模型）；索引器最后。事件 plan 归属键使用「含类型::事件名」显示串，
        // 运算符 plan 归属键使用「含类型::op 名(参数):返回」（设计 §4.6）。
        var workingRoot = root;
        if (plannedProperties.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProperties, propertyPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)rewriter.Visit(workingRoot)!;
        }
        if (plannedEvents.Count > 0)
        {
            // 属性改写产物的合成 token 间距由最终 Formatter 修复；重解析前必须先格式化，
            // 否则无空格相邻 token（如 private int / void add_E）会融合成非法标识符。
            using (var workspace = new AdhocWorkspace())
                workingRoot = (CompilationUnitSyntax)Formatter.Format(workingRoot, workspace);
            var eventTree = CSharpSyntaxTree.ParseText(
                workingRoot.ToFullString(), (CSharpParseOptions)semanticModel.SyntaxTree.Options, path: filePath);
            var eventCompilation = ((CSharpCompilation)semanticModel.Compilation)
                .ReplaceSyntaxTree(semanticModel.SyntaxTree, eventTree);
            var eventModel = eventCompilation.GetSemanticModel(eventTree);
            var eventRewriter = new Events.EventUseSiteRewriter(
                eventModel, plannedEvents, eventPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)eventRewriter.Visit(eventTree.GetRoot())!;
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
            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                semanticModel, plannedIndexers, indexerPlans,
                _options, statistics, diagnostics, filePath);
            workingRoot = (CompilationUnitSyntax)indexerRewriter.Visit(workingRoot)!;
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

    internal void ExcludePlans(IEnumerable<string> planIds)
    {
        foreach (var id in planIds) _excludedPlanIds.Add(id);
    }

    internal bool TryAttributeDiagnostic(
        Diagnostic diagnostic,
        IReadOnlyDictionary<string, string> rewrittenByFile,
        out string planId)
    {
        // 与 JK 同款：位置 → (类型名, 成员名) → 唯一 OwnsTouch plan。
        planId = string.Empty;
        if (_plans == null || diagnostic.Location.SourceTree is not { } tree) return false;
        if (!rewrittenByFile.TryGetValue(tree.FilePath, out _)) return false;

        var node = tree.GetRoot().FindNode(diagnostic.Location.SourceSpan);
        var typeName = node.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetNameString();

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in _plans)
        {
            if (_excludedPlanIds.Contains(plan.Id)) continue;
            if (!plan.TouchedFiles.Contains(tree.FilePath)) continue;
            if (plan.OwnsTouch(tree.FilePath, typeName, memberName))
                matched.Add(plan.Id);
        }
        if (matched.Count == 1)
        {
            planId = matched.First();
            return true;
        }
        return false;
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
        using (var workspace = new AdhocWorkspace())
        {
            try { text = Formatter.Format(rewritten, workspace).ToFullString(); }
            catch { text = rewritten.ToFullString(); }
        }
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
        var candidates = new List<string>();
        using (var workspace = new AdhocWorkspace())
        {
            try
            {
                var spans = root
                    .GetAnnotatedNodes(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)
                    .Where(n => n is ExpressionSyntax or StatementSyntax or MemberDeclarationSyntax)
                    .Where(n => !n.Ancestors().Any(a =>
                        a.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind)))
                    .Select(n => n.Span)
                    .Distinct()
                    .OrderBy(s => s.Start)
                    .ToList();
                if (spans.Count > 0)
                    candidates.Add(Formatter.Format(root, spans, workspace).ToFullString());
            }
            catch { }
            try { candidates.Add(Formatter.Format(root, workspace).ToFullString()); }
            catch { }
        }
        candidates.Add(root.ToFullString());

        return candidates.FirstOrDefault(candidate =>
        {
            try
            {
                var reparsed = CSharpSyntaxTree.ParseText(
                    candidate, new CSharpParseOptions(LanguageVersion.Latest));
                return !reparsed.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);
            }
            catch { return false; }
        });
    }

    private void EnsureBuilt(CSharpCompilation compilation)
    {
        if (_plans != null) return;
        var (context, plans) = MemberDesugarAnalyzer.Analyze(compilation, _options);
        _plans = plans;
        _metadataSitesByFile = context.MetadataOperatorSitesByFile;
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
