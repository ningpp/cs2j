using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.AsyncNormalize.Lambda;

namespace CSharpToJava.Core.AsyncNormalize.AsyncStreams;

/// <summary>
/// C38 3b <c>await foreach</c> 归一改写器（AS-1/AS-2/AS-3；设计 §2.1、§4.2、D2/D3/D6/D11）。
/// 展开为显式 <see cref="T:System.Collections.Generic.IAsyncEnumerator`1"/> 驱动：
/// <c>var __e = src.GetAsyncEnumerator(token); bool __ok = false; try {
/// while (await __e.MoveNextAsync()) { V x = __e.Current; BODY } __ok = true; }
/// finally { if (__ok) await __e.DisposeAsync(); else { try { await __e.DisposeAsync(); }
/// catch { /* 在途主异常优先 */ } } }</c>。
/// <see cref="AsyncNormalizeOptions.FaithfulAsyncDisposeGuard"/>=false 时退化为朴素
/// try/finally（单层 await DisposeAsync，仅供差异对照）。站点定位同 3a 纪律：
/// 入口先经 <see cref="SiteTagger"/> 在输入树 green 层挂站点/skip annotation，
/// post-C18 重解析后引用/跨度匹配仍稳定；全部合成节点挂 Cs2jAsyncNormalize annotation。
/// 嵌套展开自底向上（内外枚举器序号在 Analyzer 已独立分配）；源表达式只求值一次。
/// </summary>
internal sealed class AsyncForeachRewriter : CSharpSyntaxRewriter
{
    private const string SiteAnnotationKind = "Cs2jAsyncStreamAwaitForeachSite";

    private static readonly SyntaxAnnotation Synthetic =
        new(AsyncNormalizeAnnotations.AnnotationKind);

    private readonly AsyncForeachSite[] _sites;
    private readonly Dictionary<(int SpanStart, int RawKind), int> _siteIndexByKey = new();
    private readonly SiteTagger _tagger;
    private readonly bool _faithfulGuard;

    private bool _primed;

    public AsyncForeachRewriter(AsyncStreamAnalysisResult analysis, AsyncNormalizeOptions options)
        : base(visitIntoStructuredTrivia: false)
    {
        _sites = analysis.ForeachSites.ToArray();
        for (var i = 0; i < _sites.Length; i++)
        {
            var node = _sites[i].Node;
            _siteIndexByKey[(node.SpanStart, node.RawKind)] = i;
        }

        _tagger = new SiteTagger(
            _sites,
            analysis.ForeachRejectedNodes,
            analysis.ForeachBaselineExemptNodes,
            analysis.IteratorNodes);
        _faithfulGuard = options.FaithfulAsyncDisposeGuard;
    }

    /// <summary>是否发生任何改写（skip 标注不渲染，不计入 Changed）。</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// 在任何兄弟改写（3a await using 折叠会重包后继语句的 red 包装、改写其
    /// SpanStart）发生之前，把 foreach 站点/skip/AS-6 标注挂到输入树 green 层：
    /// 标注随 green 复用存活，之后两个 rewriter 均按标注定位，互不踩身份。
    /// </summary>
    public static SyntaxNode AttachSiteAnnotations(
        SyntaxNode root, AsyncStreamAnalysisResult analysis)
        => new SiteTagger(
            analysis.ForeachSites,
            analysis.ForeachRejectedNodes,
            analysis.ForeachBaselineExemptNodes,
            analysis.IteratorNodes).Visit(root);

    public int Expanded { get; private set; }

    public int EnumeratorsEmitted { get; private set; }

    public int WithCancellationResolved { get; private set; }

    public int ConfigureAwaitStripped { get; private set; }

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

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        if (!node.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
            return base.VisitForEachStatement(node);

        if (node.HasAnnotations(AsyncNormalizeAnnotations.SkipAnnotationKind))
            return base.VisitForEachStatement(node);

        var site = TryGetSite(node);
        if (site == null)
            return base.VisitForEachStatement(node);

        // 只下访循环体（嵌套 await foreach 在此展开）；源/token 表达式不下访——
        // 链已在分析期消解，receiver 必须只发射一次（D2/风险 9）。
        var visitedBody = (StatementSyntax)Visit(node.Statement)!;

        Changed = true;
        Expanded++;
        EnumeratorsEmitted++;
        if (site.HasWithCancellation)
            WithCancellationResolved++;
        ConfigureAwaitStripped += site.ConfigureAwaitStripped;

        return BuildExpansion(site, visitedBody)
            .WithTriviaFrom(node)
            .WithAdditionalAnnotations(Synthetic);
    }

    // ── 合成（严格按设计 §2.1） ──

    private BlockSyntax BuildExpansion(AsyncForeachSite site, StatementSyntax visitedBody)
    {
        var statements = new List<StatementSyntax>
        {
            BuildEnumeratorDeclaration(site),
        };

        TryStatementSyntax tryStatement;
        if (_faithfulGuard)
        {
            statements.Add(BuildBoolDeclaration(site.OkName));
            tryStatement = BuildFaithfulTry(site, visitedBody);
        }
        else
        {
            tryStatement = BuildPlainTry(site, visitedBody);
        }

        statements.Add(tryStatement.WithAdditionalAnnotations(Synthetic));
        return SyntaxFactory.Block(statements).WithAdditionalAnnotations(Synthetic);
    }

    private static LocalDeclarationStatementSyntax BuildEnumeratorDeclaration(AsyncForeachSite site)
    {
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                site.ReceiverExpression,
                SyntaxFactory.IdentifierName("GetAsyncEnumerator")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(site.TokenExpression))));

        var declarator = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(site.EnumeratorName))
            .WithInitializer(SyntaxFactory.EqualsValueClause(invocation));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    private static LocalDeclarationStatementSyntax BuildBoolDeclaration(string okName)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(okName))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression))))));

    private static BlockSyntax BuildWhileBody(AsyncForeachSite site, StatementSyntax visitedBody)
    {
        var statements = new List<StatementSyntax>
        {
            BuildElementDeclaration(site),
        };

        if (visitedBody is BlockSyntax bodyBlock)
            statements.AddRange(bodyBlock.Statements);
        else
            statements.Add(visitedBody);

        return SyntaxFactory.Block(statements).WithAdditionalAnnotations(Synthetic);
    }

    /// <summary>
    /// 元素声明提到 while 体内首句：var 保持 var（<c>var item = __e.Current;</c>）；
    /// 显式类型/struct/泛型参数按元素符号的 global FQF 渲染。
    /// </summary>
    private static LocalDeclarationStatementSyntax BuildElementDeclaration(AsyncForeachSite site)
    {
        TypeSyntax elementType = site.ElementTypeIsVar
            ? SyntaxFactory.IdentifierName("var")
            : SyntaxFactory.ParseTypeName(
                site.ElementType!.ToDisplayString(
                    AsyncLambdaCaptureAnalysis.GlobalFullyQualifiedFormat));

        var current = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.IdentifierName(site.EnumeratorName),
            SyntaxFactory.IdentifierName("Current"));

        var declarator = SyntaxFactory.VariableDeclarator(
                SyntaxFactory.Identifier(site.Node.Identifier.Text))
            .WithInitializer(SyntaxFactory.EqualsValueClause(current));

        return SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                elementType,
                SyntaxFactory.SingletonSeparatedList(declarator)));
    }

    private WhileStatementSyntax BuildWhileStatement(AsyncForeachSite site, BlockSyntax whileBody)
    {
        var moveNext = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(site.EnumeratorName),
                SyntaxFactory.IdentifierName("MoveNextAsync")));

        return SyntaxFactory.WhileStatement(
            SyntaxFactory.AwaitExpression(moveNext),
            whileBody);
    }

    private TryStatementSyntax BuildFaithfulTry(AsyncForeachSite site, StatementSyntax visitedBody)
    {
        var whileBody = BuildWhileBody(site, visitedBody);
        var tryBlockStatements = new List<StatementSyntax>
        {
            BuildWhileStatement(site, whileBody),
            // while 正常结束（含 break/continue/return 的正常离开路径）才置位；
            // MoveNextAsync/Current/body 任一抛出则保持 false，走在途异常分支（D6）。
            SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxFactory.IdentifierName(site.OkName),
                SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression))),
        };

        var okBranch = SyntaxFactory.Block(
            BuildDisposeStatement(site.EnumeratorName));
        var catchBlock = SyntaxFactory.Block()
            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken)
                .WithTrailingTrivia(SyntaxFactory.Space, BuildSwallowedExceptionComment(),
                    SyntaxFactory.EndOfLine("\r\n")));
        var exceptionalBranch = SyntaxFactory.Block(
            SyntaxFactory.TryStatement(
                SyntaxFactory.Block(BuildDisposeStatement(site.EnumeratorName)),
                SyntaxFactory.SingletonList<CatchClauseSyntax>(
                    SyntaxFactory.CatchClause().WithBlock(catchBlock)),
                @finally: null));

        var condition = SyntaxFactory.IdentifierName(site.OkName);
        var finallyBlock = SyntaxFactory.Block(
            SyntaxFactory.IfStatement(condition, okBranch)
                .WithElse(SyntaxFactory.ElseClause(exceptionalBranch)));

        return SyntaxFactory.TryStatement()
            .WithBlock(SyntaxFactory.Block(tryBlockStatements))
            .WithFinally(SyntaxFactory.FinallyClause(finallyBlock));
    }

    private TryStatementSyntax BuildPlainTry(AsyncForeachSite site, StatementSyntax visitedBody)
    {
        var whileBody = BuildWhileBody(site, visitedBody);
        return SyntaxFactory.TryStatement()
            .WithBlock(SyntaxFactory.Block(BuildWhileStatement(site, whileBody)))
            .WithFinally(SyntaxFactory.FinallyClause(
                SyntaxFactory.Block(BuildDisposeStatement(site.EnumeratorName))));
    }

    private static ExpressionStatementSyntax BuildDisposeStatement(string enumeratorName)
    {
        var invocation = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(enumeratorName),
                SyntaxFactory.IdentifierName("DisposeAsync")));
        return SyntaxFactory.ExpressionStatement(SyntaxFactory.AwaitExpression(invocation))
            .WithAdditionalAnnotations(Synthetic);
    }

    private static SyntaxTrivia BuildSwallowedExceptionComment()
        => SyntaxFactory.Comment(
            "/* 保真：在途主异常优先（C# 8 异步流遍历规则），释放异常丢弃不屏蔽主异常 */");

    // ── 匹配 ──

    private AsyncForeachSite? TryGetSite(ForEachStatementSyntax node)
    {
        var annotation = node.GetAnnotations(SiteAnnotationKind).FirstOrDefault();
        if (annotation?.Data is { } data && int.TryParse(data, out var index) && index >= 0
            && index < _sites.Length)
        {
            return _sites[index];
        }

        return _siteIndexByKey.TryGetValue((node.SpanStart, node.RawKind), out var keyed)
            ? _sites[keyed]
            : null;
    }

    /// <summary>
    /// 入口预处理：green 层挂 foreach 站点序号 annotation；rejected/baseline 站点与
    /// AS-6 异步迭代器成员挂 skip annotation（体内站点全部白名单、不做部分改写）。
    /// </summary>
    private sealed class SiteTagger : CSharpSyntaxRewriter
    {
        private readonly Dictionary<SyntaxNode, int> _siteIndexByReference = new();
        private readonly Dictionary<(int SpanStart, int RawKind), int> _siteIndexByKey = new();
        private readonly HashSet<SyntaxNode> _rejectedByReference = new();
        private readonly HashSet<(int SpanStart, int RawKind)> _rejectedKeys = new();
        private readonly HashSet<SyntaxNode> _baselineByReference = new();
        private readonly HashSet<(int SpanStart, int RawKind)> _baselineKeys = new();
        private readonly HashSet<SyntaxNode> _iteratorByReference = new();
        private readonly HashSet<(int SpanStart, int RawKind)> _iteratorKeys = new();

        public SiteTagger(
            IReadOnlyList<AsyncForeachSite> sites,
            IReadOnlyList<CommonForEachStatementSyntax> rejected,
            IReadOnlyList<CommonForEachStatementSyntax> baseline,
            IReadOnlyList<SyntaxNode> iterators)
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

            foreach (var node in iterators)
            {
                _iteratorByReference.Add(node);
                _iteratorKeys.Add((node.SpanStart, node.RawKind));
            }
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            if (!visited.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                return visited;
            return AnnotateIfKnown(node, visited);
        }

        public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
        {
            var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
            if (!visited.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword))
                return visited;
            return AnnotateIfKnown(node, visited);
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return IsIterator(node) ? visited.WithAdditionalAnnotations(SkipAnnotation) : visited;
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            return IsIterator(node) ? visited.WithAdditionalAnnotations(SkipAnnotation) : visited;
        }

        private bool IsIterator(SyntaxNode original)
            => _iteratorByReference.Contains(original)
            || _iteratorKeys.Contains((original.SpanStart, original.RawKind));

        private SyntaxNode AnnotateIfKnown(CommonForEachStatementSyntax original,
            CommonForEachStatementSyntax visited)
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
                return visited.WithAdditionalAnnotations(SkipAnnotation);
            }

            return visited;
        }

        private static readonly SyntaxAnnotation SkipAnnotation =
            new(AsyncNormalizeAnnotations.SkipAnnotationKind);
    }
}
