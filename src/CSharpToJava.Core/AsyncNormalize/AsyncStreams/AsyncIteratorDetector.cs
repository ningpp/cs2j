using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// 一个被冻结的异步迭代器成员（方法或局部函数，设计 D7/§3.2 AS-6）。
/// 注册序最前：成员整体 skip，其体内 await foreach/await using/async lambda
/// 在 v1 一律不做部分改写。
/// </summary>
/// <param name="Node">方法声明或局部函数声明节点。</param>
/// <param name="DisplayName">宿主成员显示名（诊断消息用）。</param>
internal sealed record AsyncIteratorMember(SyntaxNode Node, string DisplayName);

/// <summary>
/// AS-6 异步迭代器识别（设计 D7、§4.1）。命中条件全部经 SemanticModel 符号判定：
/// ① <c>async</c> 修饰；② 返回 <c>IAsyncEnumerable&lt;T&gt;</c>/<c>IAsyncEnumerator&lt;T&gt;</c>；
/// ③ 体内含 <c>yield return</c>/<c>yield break</c>。
/// 命中成员进入冻结 skip 集；<see cref="AsyncNormalizeOptions.RejectAsyncIterator"/>
/// 为 true（v1 kill-switch）时发 CS2J1844（严格 Error/非严格 Warning），false 时仅冻结
/// 不诊断——M4 联合状态机（C04 同步状态机 × C18 async builder 直积）之前无其他归宿。
/// </summary>
internal static class AsyncIteratorDetector
{
    public static IReadOnlyList<AsyncIteratorMember> Detect(
        SemanticModel model,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface,
        AsyncNormalizeOptions options,
        List<AsyncNormalizeDiagnostic> diagnostics)
    {
        var members = new List<AsyncIteratorMember>();
        if (asyncEnumerableInterface is null && asyncEnumeratorInterface is null)
            return members;

        var root = model.SyntaxTree.GetRoot();

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            Inspect(model, method, method.Identifier.ValueText,
                asyncEnumerableInterface, asyncEnumeratorInterface, options, diagnostics, members);

        foreach (var local in root.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
            Inspect(model, local, local.Identifier.ValueText,
                asyncEnumerableInterface, asyncEnumeratorInterface, options, diagnostics, members);

        return members;
    }

    private static void Inspect(
        SemanticModel model,
        SyntaxNode node,
        string name,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface,
        AsyncNormalizeOptions options,
        List<AsyncNormalizeDiagnostic> diagnostics,
        List<AsyncIteratorMember> members)
    {
        if (model.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
            return;
        if (!symbol.IsAsync)
            return;
        if (!IsAsyncStreamReturn(symbol.ReturnType, asyncEnumerableInterface, asyncEnumeratorInterface))
            return;
        if (!NodeContainsYield(node))
            return;

        members.Add(new AsyncIteratorMember(node, name));

        if (options.RejectAsyncIterator)
        {
            var kind = node is LocalFunctionStatementSyntax ? "local function" : "method";
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            diagnostics.Add(new AsyncNormalizeDiagnostic(
                AsyncNormalizeSeverity.Error,
                AsyncNormalizeDiagnosticCode.CS2J1844,
                $"Async iterator {kind} '{name}' (async + '{symbol.ReturnType.ToDisplayString()}' " +
                "return type + 'yield' in the body) cannot be normalized in v1: it requires the " +
                "joint async-iterator state machine (M4: C04 iterator regions × C18 async " +
                "builder). The whole member is left unchanged (no partial rewrite of its " +
                "await foreach/await using sites). Workaround: eagerly materialize the " +
                "sequence into a 'List<T>' and return it synchronously, or wait for M4.",
                Line: line));
        }
    }

    private static bool IsAsyncStreamReturn(
        ITypeSymbol returnType,
        INamedTypeSymbol? asyncEnumerableInterface,
        INamedTypeSymbol? asyncEnumeratorInterface)
        => AsyncTypeResolver.IsAsyncEnumerable(returnType, asyncEnumerableInterface)
        || AsyncTypeResolver.IsAsyncEnumerator(returnType, asyncEnumeratorInterface);

    private static bool NodeContainsYield(SyntaxNode node)
        => node.DescendantNodes()
            .Any(n => n.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.YieldReturnStatement)
                || n.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.YieldBreakStatement));
}
