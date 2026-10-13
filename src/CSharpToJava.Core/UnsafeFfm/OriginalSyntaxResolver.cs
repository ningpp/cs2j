using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// 把当前（已被前序规则改写的）红树节点映射回它在原始模型树中的同结构原始
/// 红节点。语法实测结论：Roslyn 的 CSharpSyntaxRewriter 即便只替换祖先
/// （如剥 unsafe 修饰符），后代红包装节点也会被重建，重建出的节点不再属于
/// 旧 SemanticModel 的 SyntaxTree（GetDeclaredSymbol/GetSymbolInfo 抛
/// 「语法节点不在语法树中」）；但不可变绿节点结构保持不变。
/// 映射方法：沿新树记录每层「第几个节点子节点」的路径，再从原始根按同路径
/// 回放，做结构等价校验（新生成节点无原始对应，返回 null）。R3 会在块内
/// 插入新语句并以 Block 替换 FixedStatement，使后续兄弟的索引整体漂移且
/// 路径深度错位：索引回放失配时按「逐层扩大回放作用域 + 跨深度等价搜索」
/// 在受限作用域（最近可回放祖先子树）内找回等价原始节点——硬性要求候选
/// 父节点种类与当前父节点相同，并按相对子节点索引路径评分取最优，不做
/// 无差别的全树搜索；另有 StripUnsafe 归一退路（R1 剥 unsafe 修饰符）。
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
        var chain = new List<(SyntaxNode Part, int Index)>();
        var current = node;

        while (current != null && !ReferenceEquals(current, _currentRoot))
        {
            var parent = current.Parent;
            if (parent == null) return null;

            var index = IndexAmongChildren(parent, current);
            if (index < 0) return null;
            chain.Add((current, index));
            current = parent;
        }

        if (current == null) return null;

        // 1) 纯索引回放（无插入/删除时最精确）。
        var indexed = ReplayByIndices(chain, chain.Count);
        if (indexed != null && EquivalentCore(indexed, node)) return indexed;

        // 2) 逐层扩大作用域的等价搜索。固定深度回放失配时（R3 以 Block 替换
        // FixedStatement 且块内插入语句，当前路径与原始路径深度错位——嵌套
        // || 还使同一名字的不同用法处在不同深度），不再只搜单一固定深度：
        // 在每个回放作用域内枚举其全部后代等价节点，硬性要求候选父节点种类
        // 与当前节点父节点种类相同，并按「相对子节点索引路径」评分，取最
        // 高分（同分时取深度更接近者）。作用域从窄到宽，命中即返回。
        var currentParentKind = node.Parent?.Kind();
        var currentIndices = chain.Select(c => c.Index).ToArray();

        for (var backtrack = 0; backtrack < chain.Count; backtrack++)
        {
            var prefixLength = chain.Count - backtrack - 1;
            var scope = ReplayByIndices(chain, prefixLength);
            if (scope == null) continue;

            var requiredDepth = chain.Count - prefixLength;

            SyntaxNode? best = null;
            var bestScore = 0;
            var bestDepthDelta = int.MaxValue;

            foreach (var candidate in scope.DescendantNodes())
            {
                if (candidate.Parent?.Kind() != currentParentKind) continue;
                if (!EquivalentCore(candidate, node)) continue;

                var candidateIndices = RelativeIndices(candidate, scope);
                var score = 0;
                var compared = Math.Min(candidateIndices.Count, requiredDepth);
                for (var p = 0; p < compared; p++)
                {
                    if (candidateIndices[p] == currentIndices[p]) score++;
                }

                var depthDelta = Math.Abs(candidateIndices.Count - requiredDepth);
                if (score > bestScore
                    || (score == bestScore && (score == 0 || depthDelta < bestDepthDelta)))
                {
                    best = candidate;
                    bestScore = score;
                    bestDepthDelta = depthDelta;
                }
            }

            if (best != null) return best;
        }

        return null;
    }

    /// <summary>节点在父节点子节点序列中的索引；非父节点直接子节点返回 -1。</summary>
    private static int IndexAmongChildren(SyntaxNode parent, SyntaxNode child)
    {
        var index = 0;
        foreach (var node in parent.ChildNodes())
        {
            if (ReferenceEquals(node, child)) return index;
            index++;
        }

        return -1;
    }

    /// <summary>
    /// 候选节点相对作用域的索引路径（候选自身在父节点中的索引在前，逐层
    /// 向上直到作用域直接子节点），与 chain 的取向一致。
    /// </summary>
    private static List<int> RelativeIndices(SyntaxNode candidate, SyntaxNode scope)
    {
        var indices = new List<int>();
        var current = candidate;
        while (current != null && !ReferenceEquals(current, scope))
        {
            var parent = current.Parent;
            if (parent == null) break;
            indices.Add(IndexAmongChildren(parent, current));
            current = parent;
        }

        return indices;
    }

    private SyntaxNode? ReplayByIndices(List<(SyntaxNode Part, int Index)> chain, int prefixLength)
    {
        var original = (SyntaxNode)_originalRoot;
        for (var i = chain.Count - 1; i >= chain.Count - prefixLength; i--)
        {
            SyntaxNode? next = null;
            var index = chain[i].Index;
            foreach (var child in original.ChildNodes())
            {
                if (index == 0)
                {
                    next = child;
                    break;
                }

                index--;
            }

            if (next == null) return null;
            original = next;
        }

        return original;
    }

    private static bool EquivalentCore(SyntaxNode original, SyntaxNode current)
    {
        if (original.IsEquivalentTo(current)) return true;

        // R1 会剥掉 unsafe 修饰符：当前树与原始树在该 token 上必然不等价。
        // R9 会剥掉 ref struct using（LocalDeclarationStatement/UsingStatement
        // 的 UsingKeyword）：比较前对两侧统一归一。
        var normalizedOriginal = NormalizeForCompare(original);
        var normalizedCurrent = NormalizeForCompare(current);
        if (normalizedOriginal.IsEquivalentTo(normalizedCurrent)) return true;

        // R1 RemoveUnsafe 会把 unsafe token 的 trivia 并入相邻保留修饰符
        // （探针实测方法同时带 method 级 unsafe 修饰时，保留修饰符尾部空白
        // 翻倍："static "→"static  "）。IsEquivalentTo 对非弹性 trivia
        // 敏感，绿结构全等仍判否；映射只用结构身份，去全部 trivia 后再比。
        return StripTrivia(normalizedOriginal)
            .IsEquivalentTo(StripTrivia(normalizedCurrent));
    }

    /// <summary>
    /// 返回去掉全部前导/尾部 trivia 的等价节点（结构不变）。仅供
    /// <see cref="EquivalentCore"/> 的 trivia 不敏感兜底比较使用。
    /// </summary>
    private static SyntaxNode StripTrivia(SyntaxNode node)
    {
        var tokens = node.DescendantTokens().ToList();
        if (tokens.Count == 0) return node;
        return node.ReplaceTokens(tokens, (_, token) => token.WithoutTrivia());
    }

    private static SyntaxNode NormalizeForCompare(SyntaxNode node)
    {
        // UnsafeStatement 的 unsafe 关键字不能用 default token 替换
        // （ArgumentException: unsafeKeyword）：把 UnsafeStatement 节点
        // 降为其内嵌 block。嵌套 unsafe 语句无法在单次 ReplaceNodes 中全
        // 收敛（外层 compute 返回原始 Block，内层节点仍在其中），故迭代。
        while (true)
        {
            var unsafeStatements = node.DescendantNodes()
                .OfType<UnsafeStatementSyntax>().ToArray();
            if (unsafeStatements.Length == 0) break;
            node = node.ReplaceNodes(
                unsafeStatements,
                (statement, _) => statement.Block);
        }

        // UsingStatement 的 UsingKeyword 同样不能以 default 替换
        // （ArgumentException: usingKeyword）：整节点降为普通 Block，
        // 声明/表达式站点提到块首，与 R9 的归一形态保持同构；同样迭代。
        while (true)
        {
            var usingStatements = node.DescendantNodes()
                .OfType<UsingStatementSyntax>().ToArray();
            if (usingStatements.Length == 0) break;
            node = node.ReplaceNodes(
                usingStatements,
                (usingNode, _) =>
                {
                    var statements = new List<StatementSyntax>();
                    if (usingNode.Declaration != null)
                    {
                        statements.Add(SyntaxFactory.LocalDeclarationStatement(
                            default, default, default,
                            usingNode.Declaration,
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
                    }
                    else if (usingNode.Expression != null)
                    {
                        statements.Add(SyntaxFactory.ExpressionStatement(usingNode.Expression));
                    }

                    if (usingNode.Statement is BlockSyntax innerBlock)
                    {
                        statements.AddRange(innerBlock.Statements);
                    }
                    else
                    {
                        statements.Add(usingNode.Statement);
                    }

                    return SyntaxFactory.Block(statements);
                });
        }

        // 剩余 unsafe 关键字（class/method/field 等声明修饰符）与 using
        // declaration 的 UsingKeyword（LocalDeclarationStatement 允许 default）
        // 可直接以 default 替换；此时树中已无 UnsafeStatement，不会触发异常。
        var replaceableTokens = node.DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.UnsafeKeyword)
                || (t.IsKind(SyntaxKind.UsingKeyword)
                    && t.Parent is LocalDeclarationStatementSyntax))
            .ToList();
        if (replaceableTokens.Count == 0) return node;

        return node.ReplaceTokens(
            replaceableTokens,
            (_, _) => default);
    }
}
