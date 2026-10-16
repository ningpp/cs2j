using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// 文件级规则接缝（C17 设计 §4.5，2026-10-01 实施校订）：numeric-boundary
/// pass 的 Rewrite 管线由显式构造的有序规则列表驱动（禁止反射发现），按
/// <see cref="Order"/> 升序执行：C17 IncrementTargets(10) → C15 CheckedArithmetic(20)
/// → C34 DecimalOps(30)（执行序以设计决策 17 为准：C17 先展开站点，C15 后扫新节点）。
/// 每条规则只消费/产出当前文件树；规则间发生树变更时由驱动方经
/// <see cref="NumericBoundaryRuleContext.RebindModel"/> 重绑语义模型。
/// </summary>
internal interface INumericBoundaryRule
{
    /// <summary>稳定规则标识（诊断/摘要用）。</summary>
    string Id { get; }

    /// <summary>文件内执行序（升序）。</summary>
    int Order { get; }

    /// <summary>规则级 kill-switch；false 时该文件零执行、零痕迹。</summary>
    bool IsEnabled(NumericBoundaryOptions options);

    /// <summary>对当前文件树执行规则；不得自行重绑模型以外的跨文件操作。</summary>
    NumericBoundaryRuleResult Execute(NumericBoundaryRuleContext context);
}

/// <summary>
/// 单文件规则执行上下文。<see cref="Root"/>/<see cref="SemanticModel"/> 随规则
/// 产出与模型重绑而更新；共享 analyzer 按当前语义模型惰性缓存，模型重绑后丢弃重建。
/// </summary>
internal sealed class NumericBoundaryRuleContext
{
    public NumericBoundaryRuleContext(
        CompilationUnitSyntax root,
        SemanticModel? semanticModel,
        bool projectDefaultChecked,
        NumericBoundaryOptions options,
        NumericBoundaryStatistics stats,
        List<NumericBoundaryDiagnostic> diagnostics,
        NumericBoundaryFileProfile? profile = null)
    {
        Root = root;
        SemanticModel = semanticModel;
        ProjectDefaultChecked = projectDefaultChecked;
        Options = options;
        Stats = stats;
        Diagnostics = diagnostics;
        Profile = profile ?? NumericBoundaryFileProfile.Scan(root);
    }

    /// <summary>当前文件树根（规则产出后由驱动方更新）。</summary>
    public CompilationUnitSyntax Root { get; set; }

    /// <summary>绑定到 <see cref="Root"/> 的语义模型；无项目语义时为 null。</summary>
    public SemanticModel? SemanticModel { get; set; }

    /// <summary>工程级 <c>/checked</c> 种子。</summary>
    public bool ProjectDefaultChecked { get; }

    /// <summary>
    /// 当前 <see cref="Root"/> 的触发语法画像（必要条件下界）。规则用它做整簇
    /// 跳过；<see cref="RebindModel"/> 产出新树后失效重建，保证画像始终描述
    /// 规则即将扫描的那棵树。
    /// </summary>
    public NumericBoundaryFileProfile Profile { get; private set; }

    /// <summary>族选项（含各规则 kill-switch）。</summary>
    public NumericBoundaryOptions Options { get; }

    /// <summary>文件级统计（规则直接写各自桶）。</summary>
    public NumericBoundaryStatistics Stats { get; }

    /// <summary>文件级诊断收集。</summary>
    public List<NumericBoundaryDiagnostic> Diagnostics { get; }

    private NumericBoundaryAnalyzer? _sharedAnalyzer;

    /// <summary>
    /// 返回按当前 <see cref="SemanticModel"/> 惰性缓存的共享 analyzer；
    /// 模型重绑后缓存丢弃，下次调用按新模型重建。
    /// </summary>
    public NumericBoundaryAnalyzer GetSharedAnalyzer()
    {
        if (_sharedAnalyzer == null)
        {
            _sharedAnalyzer = new NumericBoundaryAnalyzer(SemanticModel, ProjectDefaultChecked);
        }

        return _sharedAnalyzer;
    }

    /// <summary>
    /// 用新产出的树替换编译中的源树并重绑语义模型（逻辑逐字搬自原
    /// NumericBoundaryPreprocessor.RebuildSemanticModel）：先把新树文本解析为
    /// 语法树，存在编译级 Error 时保持现状返回 null；成功则同步更新
    /// <see cref="Root"/>/<see cref="SemanticModel"/> 并丢弃共享 analyzer 缓存。
    /// </summary>
    public SemanticModel? RebindModel(CompilationUnitSyntax newRoot)
    {
        if (SemanticModel == null)
        {
            return null;
        }

        var previousModel = SemanticModel;
        var text = SourceText.From(newRoot.ToFullString());
        var oldTree = (CSharpSyntaxTree)previousModel.SyntaxTree;
        var newTree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(text, oldTree.Options);
        if (newTree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return null;
        }

        var compilation = previousModel.Compilation.ReplaceSyntaxTree(previousModel.SyntaxTree, newTree);
        var reboundModel = compilation.GetSemanticModel(newTree);

        Root = (CompilationUnitSyntax)newTree.GetRoot();
        SemanticModel = reboundModel;
        _sharedAnalyzer = null;
        Profile = NumericBoundaryFileProfile.Scan(Root);
        return reboundModel;
    }
}

/// <summary>规则执行结果：产出树与是否发生结构变更。</summary>
internal sealed record NumericBoundaryRuleResult(CompilationUnitSyntax Root, bool MadeChanges);
