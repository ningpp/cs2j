using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// C38 3a <c>await using</c> 归一改写器（设计 §4.3 七条边界细则、D4/D8/D11）。
/// 自底向上：语句形态在 <see cref="VisitUsingStatement"/> 直接换
/// <c>资源声明 + try/finally + await DisposeAsync()</c>；声明形态在
/// <see cref="VisitBlock"/> 把后继语句序列包到块末，连续声明自然 LIFO 反向嵌套。
/// 与同步 using（无 AwaitKeyword）零交互；全部合成节点挂
/// <see cref="AsyncNormalizeAnnotations.AnnotationKind"/>，skip 节点挂
/// <see cref="AsyncNormalizeAnnotations.SkipAnnotationKind"/>（内存标注，供 residual 白名单）。
/// 站点匹配不依赖红色节点引用/SpanStart（同趟前置站点展开会改宽、C18 先行会重包祖先）：
/// 入口先经 <see cref="SiteTagger"/> 在输入树 green 层挂站点/skip annotation，
/// annotation 随 green 复用存活，之后全部按 annotation 定位计划。
/// </summary>
internal sealed class AwaitUsingRewriter : CSharpSyntaxRewriter
{
    private const string SiteAnnotationKind = "Cs2jAsyncStreamAwaitUsingSite";

    private static readonly SyntaxAnnotation Synthetic =
        new(AsyncNormalizeAnnotations.AnnotationKind);

    private readonly AwaitUsingSite[] _sites;
    private readonly Dictionary<(int SpanStart, int RawKind), int> _siteIndexByKey = new();

    private bool _primed;

    public AwaitUsingRewriter(AsyncStreamAnalysisResult analysis)
        : base(visitIntoStructuredTrivia: false)
    {
        _sites = analysis.Sites.ToArray();
        for (var i = 0; i < _sites.Length; i++)
        {
            var node = _sites[i].Node;
            _siteIndexByKey[(node.SpanStart, node.RawKind)] = i;
        }

        _tagger = new SiteTagger(
            _sites, analysis.RejectedNodes, analysis.BaselineExemptNodes);
    }

    private readonly SiteTagger _tagger;

    /// <summary>
    /// 在任何兄弟改写（3b foreach 展开会搬运宿主块语句）发生之前，把 await using
    /// 站点/skip 标注挂到输入树 green 层，跨 rewriter 重包后仍可按标注定位。
    /// </summary>
    public static SyntaxNode AttachSiteAnnotations(
        SyntaxNode root, AsyncStreamAnalysisResult analysis)
        => new SiteTagger(analysis.Sites, analysis.RejectedNodes, analysis.BaselineExemptNodes)
            .Visit(root);

    /// <summary>是否发生任何改写（skip 标注不渲染，不计入 Changed）。</summary>
    public bool Changed { get; private set; }

    public int StatementsExpanded { get; private set; }

    public int DeclarationsExpanded { get; private set; }

    public int GuardsEmitted { get; private set; }

    // ── 入口：先在输入树上打站点/skip annotation，再进入改写遍历 ──

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        if (!_primed && node is CompilationUnitSyntax root)
        {
            _primed = true;
            // preprocessor 已在所有改写前统一挂过 green 标注时不重复挂。
            if (!root.GetAnnotatedNodes(SiteAnnotationKind).Any())
                node = _tagger.Visit(root);
        }

        return base.Visit(node);
    }

    // ── 语句形态 ──

    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        if (!node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
            return base.VisitUsingStatement(node);

        if (node.HasAnnotations(AsyncNormalizeAnnotations.SkipAnnotationKind))
            return base.VisitUsingStatement(node);

        var site = TryGetSite(node);
        if (site == null)
        {
            // 无计划（理论上只可能出现在被搬运进合成树、身份丢失的克隆节点）：
            // 不动，由 residual 闸门的合成 annotation 白名单兜底。
            return base.VisitUsingStatement(node);
        }

        // 资源表达式/声明先下访（其内部 lambda 体可能嵌套 await using），体再下访。
        // 声明符下访已含其初始化器，切勿再单独下访同一表达式（否则嵌套站点计数翻倍）。
        var resources = new List<AwaitUsingResource>(site.Resources.Count);
        foreach (var resource in site.Resources)
        {
            if (resource.Declarator != null)
            {
                var visitedDeclarator = (VariableDeclaratorSyntax)Visit(resource.Declarator)!;
                resources.Add(resource with { Declarator = visitedDeclarator });
            }
            else
            {
                var visitedExpression = (ExpressionSyntax)Visit(resource.ResourceExpression)!;
                resources.Add(resource with { ResourceExpression = visitedExpression });
            }
        }

        var visitedBody = (StatementSyntax)Visit(node.Statement)!;

        Changed = true;
        StatementsExpanded++;
        return BuildResourceLevels(resources, visitedBody)
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(Synthetic);
    }

    // ── 声明形态（块级折叠到块末，连续声明 LIFO） ──

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var visited = (BlockSyntax)base.VisitBlock(node)!;
        var (folded, changed) = FoldList(visited.Statements);
        if (!changed)
            return visited;

        Changed = true;
        return visited.WithStatements(SyntaxFactory.List(folded));
    }

    /// <summary>
    /// 在语句列表中找到第一个计划内的 await using 声明站点，抽出并折叠：
    /// 声明语句子 + 后继语句序列作为最内层 try 体；多个声明符按声明序，
    /// 后一个声明包在前一个 try 内（获取失败也能正确释放先得资源），LIFO 释放。
    /// 切片直接传递仍挂父树的节点（SyntaxFactory.List 重包会产生脱父红色包装，
    /// 令引用/SpanStart 匹配失效——站点 annotation 已在 green 层，不受此影响）。
    /// </summary>
    private (IReadOnlyList<StatementSyntax> Statements, bool Changed) FoldList(
        IReadOnlyList<StatementSyntax> statements)
    {
        var firstIndex = -1;
        AwaitUsingSite? firstSite = null;
        LocalDeclarationStatementSyntax? firstNode = null;

        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not LocalDeclarationStatementSyntax { } local
                || !IsAwaitUsingDeclaration(local))
            {
                continue;
            }

            if (TryGetSite(local) is { } matched)
            {
                firstIndex = i;
                firstSite = matched;
                firstNode = local;
                break;
            }
        }

        if (firstIndex < 0)
            return (statements.ToArray(), false);

        var output = new List<StatementSyntax>(statements.Count + 2);
        for (var i = 0; i < firstIndex; i++)
            output.Add(statements[i]);

        // 从【已下访】的声明节点取声明符（保留初始化器内的嵌套改写），计划按声明序对齐。
        var resources = new List<AwaitUsingResource>(firstSite!.Resources.Count);
        for (var k = 0; k < firstSite.Resources.Count; k++)
        {
            var planned = firstSite.Resources[k];
            var visitedDeclarator = firstNode!.Declaration.Variables[k];
            resources.Add(planned with { Declarator = visitedDeclarator });
        }

        var suffix = new List<StatementSyntax>(statements.Count - firstIndex - 1);
        for (var i = firstIndex + 1; i < statements.Count; i++)
            suffix.Add(statements[i]);
        var (foldedSuffix, _) = FoldList(suffix);

        BlockSyntax BuildLevel(int index)
        {
            var levelStatements = new List<StatementSyntax>
            {
                BuildDeclarationFormLocal(resources[index], firstNode!.Declaration.Type),
            };

            BlockSyntax innerBody = index == resources.Count - 1
                ? SyntaxFactory.Block(SyntaxFactory.List(foldedSuffix))
                : BuildLevel(index + 1);

            levelStatements.Add(BuildTry(innerBody, resources[index]));
            return SyntaxFactory.Block(levelStatements).WithAdditionalAnnotations(Synthetic);
        }

        var topLevel = BuildLevel(0);

        // 最外层一层直接铺平到宿主块：`var r = …; try { … } finally { … }`。
        foreach (var topStatement in topLevel.Statements)
            output.Add(topStatement);

        DeclarationsExpanded++;
        return (output, changed: true);
    }

    // ── 节点合成 ──

    /// <summary>
    /// 语句形态（及多变量递归层）：每层 Block = 资源声明 + try，后一资源声明位于
    /// 前一资源 try 体内（声明序获取、LIFO 释放、获取中途异常先得者仍释放）。
    /// 体本身已是 Block 时直接复用，避免无意义的双层花括号。
    /// </summary>
    private BlockSyntax BuildResourceLevels(
        IReadOnlyList<AwaitUsingResource> resources,
        StatementSyntax coreBody)
    {
        BlockSyntax BuildLevel(int index)
        {
            var levelStatements = new List<StatementSyntax>
            {
                BuildStatementFormLocal(resources[index]),
            };

            BlockSyntax innerBody;
            if (index == resources.Count - 1)
                innerBody = coreBody as BlockSyntax ?? SyntaxFactory.Block(coreBody);
            else
                innerBody = BuildLevel(index + 1);

            levelStatements.Add(BuildTry(innerBody, resources[index]));
            return SyntaxFactory.Block(levelStatements).WithAdditionalAnnotations(Synthetic);
        }

        return BuildLevel(0);
    }

    private LocalDeclarationStatementSyntax BuildStatementFormLocal(AwaitUsingResource resource)
    {
        VariableDeclarationSyntax variableDeclaration;
        if (resource.Declarator != null)
        {
            // 保留源类型语法（显式类型或 var），声明符已携带下访后的初始化器。
            variableDeclaration = SyntaxFactory.VariableDeclaration(
                resource.DeclaredTypeSyntax!,
                SyntaxFactory.SingletonSeparatedList(resource.Declarator));
        }
        else
        {
            // 纯表达式形态：__cs2jAsRes_n 持有，资源只求值一次。
            var declarator = SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(resource.Name))
                .WithInitializer(SyntaxFactory.EqualsValueClause(resource.ResourceExpression));
            variableDeclaration = SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(declarator));
        }

        return SyntaxFactory.LocalDeclarationStatement(variableDeclaration)
            .WithAdditionalAnnotations(Synthetic);
    }

    private LocalDeclarationStatementSyntax BuildDeclarationFormLocal(
        AwaitUsingResource resource,
        TypeSyntax declaredType)
    {
        // 声明形态恒有声明符；剥离 using/await 关键字即为普通局部声明，
        // 初始化器（含 await FactoryAsync()）原位保留、仍在 try 外。
        var variableDeclaration = SyntaxFactory.VariableDeclaration(
            declaredType,
            SyntaxFactory.SingletonSeparatedList(resource.Declarator!));
        return SyntaxFactory.LocalDeclarationStatement(variableDeclaration)
            .WithAdditionalAnnotations(Synthetic);
    }

    private TryStatementSyntax BuildTry(BlockSyntax body, AwaitUsingResource resource)
    {
        var finallyStatement = BuildDisposeStatement(resource);
        var tryStatement = SyntaxFactory.TryStatement()
            .WithBlock(body)
            .WithFinally(SyntaxFactory.FinallyClause(SyntaxFactory.Block(finallyStatement)));

        if (resource.IsReferenceType || resource.IsNullableValueType)
            GuardsEmitted++;

        return tryStatement.WithAdditionalAnnotations(Synthetic);
    }

    /// <summary>
    /// 释放语句：引用类型 <c>if (r != null) await r.DisposeAsync();</c>；
    /// <c>S?</c> 资源 <c>if (r.HasValue) await r.Value.DisposeAsync();</c>；
    /// 普通 struct/非可空值类型直接调用；显式接口实现经 <c>((IAsyncDisposable)r)</c> 绑定（D8）。
    /// await 原样保留（finally 内 await 合法，基线保证）。
    /// </summary>
    private StatementSyntax BuildDisposeStatement(AwaitUsingResource resource)
    {
        ExpressionSyntax receiver = SyntaxFactory.IdentifierName(resource.Name);
        if (resource.IsNullableValueType)
        {
            receiver = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName("Value"));
        }

        if (resource.UseInterfaceCast)
        {
            var interfaceType = SyntaxFactory.ParseTypeName("global::System.IAsyncDisposable");
            receiver = SyntaxFactory.ParenthesizedExpression(
                SyntaxFactory.CastExpression(interfaceType, receiver));
        }

        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName("DisposeAsync")));
        var disposeStatement = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.AwaitExpression(invocation));

        if (resource.IsReferenceType)
        {
            var notNull = SyntaxFactory.BinaryExpression(
                SyntaxKind.NotEqualsExpression,
                SyntaxFactory.IdentifierName(resource.Name),
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            return SyntaxFactory.IfStatement(notNull, disposeStatement)
                .WithAdditionalAnnotations(Synthetic);
        }

        if (resource.IsNullableValueType)
        {
            var hasValue = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(resource.Name),
                SyntaxFactory.IdentifierName("HasValue"));
            return SyntaxFactory.IfStatement(hasValue, disposeStatement)
                .WithAdditionalAnnotations(Synthetic);
        }

        return disposeStatement.WithAdditionalAnnotations(Synthetic);
    }

    // ── 匹配 ──

    private AwaitUsingSite? TryGetSite(StatementSyntax node)
    {
        var annotation = node.GetAnnotations(SiteAnnotationKind).FirstOrDefault();
        if (annotation?.Data is { } data && int.TryParse(data, out var index) && index >= 0
            && index < _sites.Length)
        {
            return _sites[index];
        }

        // 兜底：输入树未经打标（直接复用本改写器的理论路径）时退回双键匹配。
        return _siteIndexByKey.TryGetValue((node.SpanStart, node.RawKind), out var keyed)
            ? _sites[keyed]
            : null;
    }

    private static bool IsAwaitUsingDeclaration(LocalDeclarationStatementSyntax local)
        => local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
        && local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);

    /// <summary>
    /// 入口预处理：在输入树 green 层给计划站点挂带序号的站点 annotation、
    /// 给 rejected/baseline 站点挂 skip annotation。annotation 不占文本宽度，
    /// 随后续节点 green 复用在祖先重包/同趟改宽中存活，规避红色引用与 SpanStart 漂移。
    /// 匹配先用引用，失败退回 (SpanStart, RawKind)（C18 先行后未改宽的同层兄弟仍可命中）。
    /// </summary>
    private sealed class SiteTagger : CSharpSyntaxRewriter
    {
        private readonly Dictionary<SyntaxNode, int> _siteIndexByReference = new();
        private readonly Dictionary<(int SpanStart, int RawKind), int> _siteIndexByKey = new();
        private readonly HashSet<SyntaxNode> _rejectedByReference = new();
        private readonly HashSet<(int SpanStart, int RawKind)> _rejectedKeys = new();
        private readonly HashSet<SyntaxNode> _baselineByReference = new();
        private readonly HashSet<(int SpanStart, int RawKind)> _baselineKeys = new();

        public SiteTagger(
            IReadOnlyList<AwaitUsingSite> sites,
            IReadOnlyList<StatementSyntax> rejected,
            IReadOnlyList<StatementSyntax> baseline)
            : base(visitIntoStructuredTrivia: false)
        {
            for (var i = 0; i < sites.Count; i++)
            {
                var node = sites[i].Node;
                _siteIndexByReference[node] = i;
                _siteIndexByKey[(node.SpanStart, node.RawKind)] = i;
            }

            foreach (var node in rejected)
            {
                _rejectedByReference.Add(node);
                _rejectedKeys.Add((node.SpanStart, node.RawKind));
            }

            foreach (var node in baseline)
            {
                _baselineByReference.Add(node);
                _baselineKeys.Add((node.SpanStart, node.RawKind));
            }
        }

        public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
        {
            var visited = (UsingStatementSyntax)base.VisitUsingStatement(node)!;
            if (!visited.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                return visited;
            return AnnotateIfKnown(node, visited);
        }

        public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
        {
            var visited = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
            if (!IsAwaitUsingDeclaration(node))
                return visited;
            return AnnotateIfKnown(node, visited);
        }

        private SyntaxNode AnnotateIfKnown(StatementSyntax original, StatementSyntax visited)
        {
            if (_siteIndexByReference.TryGetValue(original, out var index)
                || _siteIndexByKey.TryGetValue((original.SpanStart, original.RawKind), out index))
            {
                return visited.WithAdditionalAnnotations(
                    new SyntaxAnnotation(SiteAnnotationKind, index.ToString()));
            }

            if (_rejectedByReference.Contains(original)
                || _rejectedKeys.Contains((original.SpanStart, original.RawKind))
                || _baselineByReference.Contains(original)
                || _baselineKeys.Contains((original.SpanStart, original.RawKind)))
            {
                return visited.WithAdditionalAnnotations(
                    new SyntaxAnnotation(AsyncNormalizeAnnotations.SkipAnnotationKind));
            }

            return visited;
        }

        private static bool IsAwaitUsingDeclaration(LocalDeclarationStatementSyntax local)
            => local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword)
            && local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);
    }
}
