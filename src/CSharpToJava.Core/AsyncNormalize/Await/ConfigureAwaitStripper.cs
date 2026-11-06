using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.Await;

/// <summary>
/// ConfigureAwait(bool) 剥离的判定/计数共享体（1b）：
/// AsyncLambdaLifter 在「原始树遍历」中同步剥离（提升方法体在搬运时已脱离原树，
/// 二趟 rewriter 无法再用原 SemanticModel 绑定），随后的 <see cref="AwaitShapeRewriter"/>
/// 作为无计划/kill-switch 场景的独立执行体；两者共用同一计数器与诊断集，
/// 保证每个站点只计一次（剥过的节点二趟不再匹配）。
/// </summary>
internal sealed class ConfigureAwaitStripper
{
    public int ConfigureAwaitStripped { get; private set; }

    public List<AsyncNormalizeDiagnostic> Diagnostics { get; } = new();

    /// <summary>
    /// 仅做「是否应剥离」判定。<paramref name="node"/> 必须仍属于 <paramref name="model"/>
    /// 绑定的原始语法树（二趟遍历遇到的合成/重建节点不得调用本方法）。
    /// </summary>
    public bool ShouldStrip(SemanticModel model, InvocationExpressionSyntax node)
    {
        if (node.Expression is not MemberAccessExpressionSyntax)
            return false;

        return AsyncShapeClassifier.IsConfigureAwaitBool(model, node, out var isOptionsForm)
            && !isOptionsForm;
    }

    public void Record(InvocationExpressionSyntax node)
    {
        ConfigureAwaitStripped++;
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        Diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Info,
            AsyncNormalizeDiagnosticCode.CS2J1830,
            $"ConfigureAwait(bool) stripped at line {line}; the 'await' is preserved " +
            "(Java has no SynchronizationContext to capture, so dropping the continuation " +
            "capture does not change the target semantics).",
            Line: line));
    }

    /// <summary>
    /// 独立 await 表达式语句上的 options 形态语义等价归一化：
    /// 含 <c>SuppressThrowing</c> → <c>try { await task; } catch { }</c>（等待并吞掉任务
    /// 异常/取消，与 options 语义一致）；其余 options → <c>await task</c>（等价于剥离
    /// <c>ConfigureAwait(false)</c>，Java 无同步上下文）。仅当调用符号绑定到
    /// Task/ValueTask 的 <c>ConfigureAwait(ConfigureAwaitOptions)</c> 且 options 为常量时成立。
    /// </summary>
    public bool TryRewriteOptionsStatement(
        SemanticModel model,
        ExpressionStatementSyntax statement,
        out StatementSyntax? rewritten,
        out bool suppressThrowing)
    {
        rewritten = null;
        suppressThrowing = false;

        if (statement.Expression is not AwaitExpressionSyntax awaitExpression)
            return false;
        if (awaitExpression.Expression is not InvocationExpressionSyntax invocation
            || invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return false;
        if (!AsyncShapeClassifier.IsNormalizableOptionsAwait(model, invocation, out suppressThrowing))
            return false;

        var awaitStripped = SyntaxFactory.AwaitExpression(memberAccess.Expression.WithoutTrivia())
            .WithTriviaFrom(awaitExpression);
        StatementSyntax result = SyntaxFactory.ExpressionStatement(awaitStripped);

        if (suppressThrowing)
        {
            result = SyntaxFactory.TryStatement(
                SyntaxFactory.Block(result),
                SyntaxFactory.SingletonList(SyntaxFactory.CatchClause(
                    declaration: null,
                    filter: null,
                    block: SyntaxFactory.Block())),
                @finally: null);
        }

        rewritten = result.WithTriviaFrom(statement);
        return true;
    }

    public void RecordOptions(ExpressionStatementSyntax node, bool suppressThrowing)
    {
        ConfigureAwaitStripped++;
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var shape = suppressThrowing
            ? "ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing) on a standalone await " +
              "statement normalized to 'try { await task; } catch { }'"
            : "ConfigureAwait(ConfigureAwaitOptions) on a standalone await statement " +
              "normalized to 'await task' (equivalent to stripping ConfigureAwait(false))";
        Diagnostics.Add(new AsyncNormalizeDiagnostic(
            AsyncNormalizeSeverity.Info,
            AsyncNormalizeDiagnosticCode.CS2J1830,
            $"{shape} at line {line}; Java has no SynchronizationContext to capture, so the " +
            "target semantics are preserved.",
            Line: line));
    }
}
