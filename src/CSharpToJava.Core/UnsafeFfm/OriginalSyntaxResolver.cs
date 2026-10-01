using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// 把当前（已被前序规则改写的）红树节点映射回它在原始模型树中的同结构原始
/// 红节点。语法实测结论：Roslyn 的 CSharpSyntaxRewriter 即便只替换祖先
/// （如剥 unsafe 修饰符），后代红包装节点也会被重建，重建出的节点不再属于
/// 旧 SemanticModel 的 SyntaxTree（GetDeclaredSymbol/GetSymbolInfo 抛
/// 「语法节点不在语法树中」）；但不可变绿节点结构保持不变。
/// 映射方法：沿新树记录每层「第几个节点子节点」的路径，再从原始根按同路径
/// 回放，最后做结构等价校验（新生成节点无原始对应，返回 null）。
/// </summary>
internal sealed class OriginalSyntaxResolver
{
    private readonly SyntaxNode _originalRoot;
    private readonly SyntaxNode _currentRoot;
    private readonly ConditionalWeakTable<SyntaxNode, SyntaxNode?> _cache = new();

    public OriginalSyntaxResolver(SyntaxNode originalRoot, SyntaxNode currentRoot)
    {
        _originalRoot = originalRoot;
        _currentRoot = currentRoot;
    }

    /// <summary>
    /// 返回 <paramref name="node"/> 在原始模型树中的等价节点；不存在（生成节点）
    /// 或无法证明时返回 null。
    /// </summary>
    public SyntaxNode? Resolve(SyntaxNode node)
    {
        if (ReferenceEquals(node, _currentRoot)) return _originalRoot;
        if (_cache.TryGetValue(node, out var cached)) return cached;

        cached = ResolveCore(node);
        _cache.Add(node, cached);
        return cached;
    }

    public T? Resolve<T>(T node) where T : SyntaxNode
        => Resolve((SyntaxNode)node) as T;

    private SyntaxNode? ResolveCore(SyntaxNode node)
    {
        var path = new List<int>();
        var current = node;

        while (current != null && !ReferenceEquals(current, _currentRoot))
        {
            var parent = current.Parent;
            if (parent == null) return null;

            var index = 0;
            var found = false;
            foreach (var child in parent.ChildNodes())
            {
                if (ReferenceEquals(child, current))
                {
                    found = true;
                    break;
                }

                index++;
            }

            if (!found) return null;
            path.Add(index);
            current = parent;
        }

        if (current == null) return null;

        var original = _originalRoot;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            var children = original.ChildNodes();
            var index = path[i];
            var found = false;
            foreach (var child in children)
            {
                if (index == 0)
                {
                    original = child;
                    found = true;
                    break;
                }

                index--;
            }

            if (!found) return null;
        }

        if (original.IsEquivalentTo(node)) return original;

        // R1 会剥掉 unsafe 修饰符：当前树与原始树在该 token 上必然不等价。
        // 退路：两边同时移除 unsafe keyword 后再做结构等价。
        var normalizedOriginal = StripUnsafe(original);
        var normalizedCurrent = StripUnsafe(node);
        return normalizedOriginal.IsEquivalentTo(normalizedCurrent) ? original : null;
    }

    private static SyntaxNode StripUnsafe(SyntaxNode node)
    {
        var unsafeTokens = node.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.UnsafeKeyword))
            .ToList();
        if (unsafeTokens.Count == 0) return node;

        return node.ReplaceTokens(
            unsafeTokens,
            (_, _) => default);
    }
}
