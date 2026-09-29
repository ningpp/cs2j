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

        var active = _plans!
            .Where(p => !_excludedPlanIds.Contains(p.Id) && p.TouchedFiles.Contains(filePath))
            .ToList();

        foreach (var p in active.Where(p => p.Outcome != MemberDesugarOutcome.Planned))
            EmitSkipDiagnostics(p, filePath, statistics, diagnostics);

        var plannedProps = active.OfType<Properties.PropertyPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        var plannedIndexers = active.OfType<Indexers.IndexerPlan>()
            .Where(p => p.Outcome == MemberDesugarOutcome.Planned).ToList();
        if (plannedProps.Count == 0 && plannedIndexers.Count == 0)
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }
        statistics.PropertiesPlanned += plannedProps.Count(p => IsFirstDeclarationFile(p, filePath));
        statistics.IndexersPlanned += plannedIndexers.Count(p => IsFirstDeclarationFile(p, filePath));

        // 属性 pass 先行（Order 10 → 20）；两族都改写同一文件时，索引器 pass 必须基于
        // 重解析树重新绑定（SemanticModel 只能绑定自身语法树的节点，设计 §4.2-3）。
        var current = (SyntaxNode)root;
        var currentModel = semanticModel;
        if (plannedProps.Count > 0)
        {
            var rewriter = new Properties.PropertyUseSiteRewriter(
                semanticModel, plannedProps, active.OfType<Properties.PropertyPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            var visited = (CompilationUnitSyntax)rewriter.Visit(root)!;
            if (!visited.IsEquivalentTo(root))
            {
                (currentModel, current) = RebindModel(semanticModel, visited);
            }
        }
        if (plannedIndexers.Count > 0)
        {
            var indexerRewriter = new Indexers.IndexerUseSiteRewriter(
                currentModel, plannedIndexers, active.OfType<Indexers.IndexerPlan>().ToList(),
                _options, statistics, diagnostics, filePath);
            current = indexerRewriter.Visit(current)!;
        }
        var newRoot = (CompilationUnitSyntax)current;
        if (newRoot.IsEquivalentTo(root))
        {
            statistics.FilesSkippedClean++;
            return new MemberDesugarFileResult { OutputCode = sourceCode, Changed = false };
        }

        var outputCode = FormatAndValidate(newRoot);
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
        var (_, plans) = MemberDesugarAnalyzer.Analyze(compilation, _options);
        _plans = plans;
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
            diagnostics.Add(new MemberDesugarDiagnostic(
                MemberDesugarSeverity.Info, MemberDesugarDiagnosticCode.PlanCreated,
                "property plan skipped by overrides.skip configuration",
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
        var code = p.FailCode ?? MemberDesugarDiagnosticCode.ResidualProperty;
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
