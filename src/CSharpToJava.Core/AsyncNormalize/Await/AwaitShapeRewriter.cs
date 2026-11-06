using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.Await;

/// <summary>
/// 轻量版唯一强制改写（设计 §2 D5、§4.3）：符号绑定到
/// Task/ValueTask(T) 的 <c>ConfigureAwait(bool)</c> 调用整体替换为 receiver 表达式，
/// <c>await</c> 原样保留；独立 await 表达式语句上的
/// <c>ConfigureAwait(ConfigureAwaitOptions)</c> 按常量 options 归一化（含
/// SuppressThrowing → try/catch 吞异常，其余 → 剥离），其余 options 形态由 Analyzer
/// 发 CS2J1808。判定全部经 SemanticModel 符号绑定，自定义同名扩展不动。
/// 1b 起：在含提升的文件上本 rewriter 作为二趟安全网运行——外提构造出的新树节点
/// 不属于原 SemanticModel，这类节点的剥离已由 AsyncLambdaLifter 在原始树遍历中完成；
/// 此处对不可绑定节点直接下访，不向原模型查询。
/// 幂等：重跑无 ConfigureAwait(bool) 即零改写。
/// </summary>
internal sealed class AwaitShapeRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly ConfigureAwaitStripper _stripper;

    public AwaitShapeRewriter(SemanticModel model, ConfigureAwaitStripper? stripper = null)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
        _stripper = stripper ?? new ConfigureAwaitStripper();
    }

    public int ConfigureAwaitStripped => _stripper.ConfigureAwaitStripped;

    public List<AsyncNormalizeDiagnostic> Diagnostics => _stripper.Diagnostics;

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // 独立 await 表达式语句上的 ConfigureAwait(ConfigureAwaitOptions)：
        // 含 SuppressThrowing → try/catch 吞异常；其余 options → 剥离为 await task。
        if (BelongsToBoundTree(node)
            && node.Expression is AwaitExpressionSyntax
            && _stripper.TryRewriteOptionsStatement(_model, node, out var rewritten, out var suppressThrowing))
        {
            _stripper.RecordOptions(node, suppressThrowing);
            return rewritten!.WithTriviaFrom(node);
        }

        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 仅当节点仍属于本 rewriter 绑定的原始语法树时才做符号判定。
        // lift 后新树中的重建/合成节点（含 wrapper 转发调用）不可绑定，
        // 其 ConfigureAwait 站点已在 lifter 遍历原始树时剥净。
        if (BelongsToBoundTree(node) && _stripper.ShouldStrip(_model, node)
            && node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            _stripper.Record(node);
            var rewrittenReceiver = (ExpressionSyntax)Visit(memberAccess.Expression)!;
            return rewrittenReceiver.WithTriviaFrom(node);
        }

        return base.VisitInvocationExpression(node);
    }

    private bool BelongsToBoundTree(SyntaxNode node)
        => node.SyntaxTree != null && ReferenceEquals(node.SyntaxTree, _model.SyntaxTree);
}
