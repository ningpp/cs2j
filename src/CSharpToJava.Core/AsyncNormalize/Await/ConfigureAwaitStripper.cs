using Microsoft.CodeAnalysis;
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
}
