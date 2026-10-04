using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.Await;

/// <summary>
/// 轻量版唯一强制改写（设计 §2 D5、§4.3）：符号绑定到
/// Task/ValueTask(T) 的 <c>ConfigureAwait(bool)</c> 调用整体替换为 receiver 表达式，
/// <c>await</c> 原样保留；<c>ConfigureAwait(ConfigureAwaitOptions)</c> 不在此改写
/// （由 Analyzer 发 CS2J1808）。判定全部经 SemanticModel 符号绑定，自定义同名扩展不动。
/// 幂等：重跑无 ConfigureAwait(bool) 即零改写。
/// </summary>
internal sealed class AwaitShapeRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;

    public AwaitShapeRewriter(SemanticModel model)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
    }

    public int ConfigureAwaitStripped { get; private set; }

    public List<AsyncNormalizeDiagnostic> Diagnostics { get; } = new();

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 先在原始节点上做符号判定（子节点改写后新节点不在原 SemanticModel 树内），
        // 判定成立时再递归改写 receiver，使链式 x.ConfigureAwait(false).ConfigureAwait(false)
        // 逐层剥净。
        if (AsyncShapeClassifier.IsConfigureAwaitBool(_model, node, out var isOptionsForm)
            && !isOptionsForm
            && node.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            ConfigureAwaitStripped++;
            Diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Info,
                AsyncNormalizeDiagnosticCode.CS2J1830,
                $"ConfigureAwait(bool) stripped at line {line}; the 'await' is preserved " +
                "(Java has no SynchronizationContext to capture, so dropping the continuation " +
                "capture does not change the target semantics).",
                Line: line));

            var rewrittenReceiver = (ExpressionSyntax)Visit(memberAccess.Expression)!;
            return rewrittenReceiver.WithTriviaFrom(node);
        }

        return base.VisitInvocationExpression(node);
    }
}
