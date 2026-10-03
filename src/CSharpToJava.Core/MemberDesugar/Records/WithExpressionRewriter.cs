using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.MemberDesugar.Properties;

namespace CSharpToJava.Core.MemberDesugar.Records;

/// <summary>
/// WR-1/WR-2/WR-3/WR-6/WR-8 with 使用点改写（设计 §4.3/决策 9/10）。单遍遍历原始树，
/// 所有结论（站点分类/闸门/工厂定名/左值成员符号）全部来自分析期 plan，运行期零文本判定：
/// <list type="bullet">
/// <item>空初始化器（WR-3）：内联为 <c>recv.Cs2jWithClone()</c>，无需语句；</item>
/// <item>非空 record class 站点（WR-1/2/6）：源接收者只求值一次（非简单接收者先提升
/// <c>__cs2j_wsrc</c>），克隆体 <c>__cs2j_wclone</c>，属性左值发 set_X(e)、字段左值
/// 直写；多态只靠虚工厂（禁止 switch-on-type）；类型内 <c>this with</c> 同形；</item>
/// <item>闸门/跳过站点：原子不动并逐站点发 52xx 诊断，节点挂跳过标注；</item>
/// <item>WR-8：<see cref="ReconcileResidual"/> 对账——输出树残留无标注的 with 即 5230。</item>
/// </list>
/// 语句提升/块拼接纪律与 C11 PropertyUseSiteRewriter 同形（块/switch 节/全局语句）。
/// record 声明部件的工厂注入（RC-1/RC-2）在同遍游历中经
/// <see cref="RecordDeclarationRewriter"/> 完成，保证语义查询全部落在原始在树节点上。
/// </summary>
internal sealed class WithExpressionRewriter : CSharpSyntaxRewriter
{
    /// <summary>跳过族站点标注：挂在保留不动的 with 节点上，供 WR-8 residual 对账。</summary>
    private static readonly SyntaxAnnotation SkipAnnotation = new("Cs2jRecordWithSkipped");

    private readonly Dictionary<WithExpressionSyntax, (RecordClonePlan Plan, WithSite Site)> _siteMap =
        new(SyntaxReferenceEqualityComparer.Instance);
    private readonly HashSet<WithExpressionSyntax> _exemptSites =
        new(SyntaxReferenceEqualityComparer.Instance);
    private readonly Dictionary<int, RecordClonePlan> _planByInjectSpan = new();
    private readonly HashSet<string> _skippedPlanIds = new(StringComparer.Ordinal);

    private readonly MemberDesugarOptions _options;
    private readonly MemberDesugarStatistics _stats;
    private readonly List<MemberDesugarDiagnostic> _diagnostics;
    private readonly string _filePath;
    private int _tempCounter;

    public WithExpressionRewriter(
        IReadOnlyList<RecordClonePlan> activePlansForFile,
        IReadOnlyList<RecordClonePlan> allPlansForFile,
        MemberDesugarOptions options,
        MemberDesugarStatistics stats,
        List<MemberDesugarDiagnostic> diagnostics,
        string filePath)
    {
        _options = options;
        _stats = stats;
        _diagnostics = diagnostics;
        _filePath = filePath;

        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in activePlansForFile)
        {
            activeIds.Add(plan.Id);
            if (!plan.IsOrphan && plan.FactoryInsertSpanStart is { } span)
                _planByInjectSpan[span] = plan;
            foreach (var site in plan.Sites)
            {
                if (!string.Equals(site.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                _siteMap[site.Syntax] = (plan, site);
            }
        }

        // 适配器 ExcludePlans 回退的 plan：站点保留、挂豁免标注（框架已发 5101），
        // 不重发诊断、不计 residual（与 FinalizerRewriter _exemptKeys 同形）。
        foreach (var plan in allPlansForFile)
        {
            if (activeIds.Contains(plan.Id)) continue;
            foreach (var site in plan.Sites)
            {
                if (string.Equals(site.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    _exemptSites.Add(site.Syntax);
            }
        }
    }

    /// <summary>本文件是否实际发生了树改写（声明注入或站点改写）。</summary>
    public bool Changed { get; private set; }

    // ---- record 声明：工厂注入（RC-1/RC-2），部件按 plan 期 SpanStart 精确匹配 ----

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var visited = (RecordDeclarationSyntax?)base.VisitRecordDeclaration(node);
        if (visited == null) return null;
        // plan.FactoryInsertSpanStart 取 INamedTypeSymbol 首个源内 location——其跨度锚定
        // 声明标识符（非 record 关键字），partial 多部件各自一个标识符 location，天然去重。
        if (!_planByInjectSpan.TryGetValue(node.Identifier.SpanStart, out var plan))
            return visited;

        // RC-3/RC-4：先在同部件发射 reshape 终形（去 ParameterList + 字段/get/set/ctor/
        // Deconstruct），再在终形上注入克隆工厂（同一 Visit 遍次，同改同不改）。
        // 注意：部件身份/跨度判定一律用绿色原节点 node；visited 是 base.Visit 产出的红色
        // 脱离节点，其 SpanStart 只是「子树相对偏移」（脱离红节点挂在合成子树上），有
        // using/前文时与文件绝对跨度不一致，不能拿它跟 plan/shape 的锚点跨度比。
        var reshaped = RecordDeclarationRewriter.TryApplyReshape(
                node, visited, plan, _options, _stats, _diagnostics, _filePath)
            ?? visited;
        if (!ReferenceEquals(reshaped, visited)) Changed = true;

        var factory = RecordDeclarationRewriter.TryBuildFactory(
            reshaped, plan, _options, _stats, _diagnostics, _filePath);
        if (factory == null) return reshaped;
        Changed = true;
        return reshaped.AddMembers(factory);
    }

    // ---- 语句拼接：提升产物块拆入外层作用域（与 C11 同形，临时变量须对后续语句可见）----

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var members = new List<MemberDeclarationSyntax>();
        foreach (var member in node.Members)
        {
            if (member is GlobalStatementSyntax global)
            {
                if (Visit(global.Statement) is not StatementSyntax visited) continue;
                if (visited is BlockSyntax spliced
                    && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
                {
                    var parts = Splice(spliced).ToList();
                    parts[0] = parts[0].WithLeadingTrivia(
                        global.GetLeadingTrivia().Concat(parts[0].GetLeadingTrivia()));
                    foreach (var s in parts)
                        members.Add(SyntaxFactory.GlobalStatement(s));
                    continue;
                }
                members.Add(global.WithStatement(visited));
                continue;
            }
            if (Visit(member) is MemberDeclarationSyntax visitedMember)
                members.Add(visitedMember);
        }
        return node.WithMembers(SyntaxFactory.List(members));
    }

    public override SyntaxNode? VisitBlock(BlockSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    public override SyntaxNode? VisitSwitchSection(SwitchSectionSyntax node)
        => node.WithStatements(SyntaxFactory.List(SpliceStatements(node.Statements)));

    private IEnumerable<StatementSyntax> SpliceStatements(SyntaxList<StatementSyntax> statements)
    {
        foreach (var stmt in statements)
        {
            if (Visit(stmt) is not StatementSyntax visited) continue;
            if (visited is BlockSyntax spliced
                && spliced.HasAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation.Kind))
            {
                foreach (var s in Splice(spliced)) yield return s;
                continue;
            }
            yield return visited;
        }
    }

    private static IEnumerable<StatementSyntax> Splice(BlockSyntax block)
    {
        var list = block.Statements.ToList();
        if (list.Count == 0) yield break;
        list[0] = list[0].WithLeadingTrivia(
            block.GetLeadingTrivia().Concat(list[0].GetLeadingTrivia().Where(t => t.IsDirective)));
        list[^1] = list[^1].WithTrailingTrivia(block.GetTrailingTrivia());
        foreach (var s in list) yield return s;
    }

    // ---- 语句级提升位（决策 10；可提升性已在分析期 IsLiftableSite 闸门保证）----

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (StripParens(node.Expression) is WithExpressionSyntax w
            && TryGetRewritable(w, out var plan, out var site))
        {
            var (block, _) = BuildLiftedBlock(w, plan!, site!, tail: null);
            return block
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        return base.VisitExpressionStatement(node);
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (node.Declaration.Variables.Count == 1
            && node.Declaration.Variables[0].Initializer?.Value is { } initializer
            && StripParens(initializer) is WithExpressionSyntax w
            && TryGetRewritable(w, out var plan, out var site))
        {
            var (block, cloneName) = BuildLiftedBlock(w, plan!, site!, tail: null);
            var declarator = node.Declaration.Variables[0]
                .WithInitializer(SyntaxFactory.EqualsValueClause(Identifier(cloneName)));
            var final = node.WithDeclaration(
                node.Declaration.WithVariables(SyntaxFactory.SingletonSeparatedList(declarator)));
            return block.AddStatements(final)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        return base.VisitLocalDeclarationStatement(node);
    }

    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
    {
        if (node.Expression is { } returned
            && StripParens(returned) is WithExpressionSyntax w
            && (node.Parent is BlockSyntax || node.Parent is SwitchSectionSyntax)
            && TryGetRewritable(w, out var plan, out var site))
        {
            var (block, cloneName) = BuildLiftedBlock(w, plan!, site!, tail: null);
            var @return = SyntaxFactory.ReturnStatement(
                Identifier(cloneName).WithLeadingTrivia(SyntaxFactory.Space));
            return block.AddStatements(@return)
                .WithLeadingTrivia(node.GetLeadingTrivia())
                .WithTrailingTrivia(node.GetTrailingTrivia());
        }
        return base.VisitReturnStatement(node);
    }

    // ---- 箭头体提升：表达式体成员/表达式 lambda 的 with 整体把 ArrowExpressionClause
    // 换成「提升序列 + return clone」块（决策 10 扩展；站点直接性与可提升性已在分析期
    // IsLiftableSite 闸门保证）。访问全程只读绿色原始节点，红块直接返回不重入 Visit。----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (TryGetArrowSite(node.ExpressionBody, node.Body,
                out var w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            return node.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body)
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitMethodDeclaration(node);
    }

    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        if (TryGetArrowSite(node.ExpressionBody, node.Body,
                out var w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            return node.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body)
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitOperatorDeclaration(node);
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        if (TryGetArrowSite(node.ExpressionBody, node.Body,
                out var w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            return node.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body)
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitConversionOperatorDeclaration(node);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (TryGetArrowSite(node.ExpressionBody, node.Body,
                out var w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            // 本地函数是语句：块是函数自有作用域，不拆入外层（外层 SpliceStatements
            // 仅拆 BlockSyntax 产物，本节点保持 LocalFunctionStatement 原位）。
            return node.WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(body)
                .WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitLocalFunctionStatement(node);
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        // lambda 的 ExpressionBody 直接是表达式（无 ArrowExpressionClause 包裹）。
        if (node.Block == null
            && node.ExpressionBody is { } lambdaBody
            && StripParens(lambdaBody) is WithExpressionSyntax w
            && TryGetRewritable(w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            // 表达式 lambda → 语句 lambda：operatorToken（=>）保留，仅把 ExpressionBody
            // 换成 Block；块是 lambda 自有作用域，绝不拆入外层语句序列。
            return node.WithExpressionBody(null)
                .WithBlock(body)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitParenthesizedLambdaExpression(node);
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        if (node.Block == null
            && node.ExpressionBody is { } lambdaBody
            && StripParens(lambdaBody) is WithExpressionSyntax w
            && TryGetRewritable(w, out var plan, out var site))
        {
            var body = BuildArrowBodyBlock(w, plan!, site!);
            return node.WithExpressionBody(null)
                .WithBlock(body)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitSimpleLambdaExpression(node);
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        // 声明级箭头属性 public T P => r with {...}; → { get { 提升; return clone; } }，
        // 下游 C11 PropertyDeclarationExpander 按带块访问器展成 get_P()。
        if (TryGetArrowSite(node.ExpressionBody, null,
                out var w, out var plan, out var site))
        {
            var updated = node
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithBody(BuildArrowBodyBlock(w, plan!, site!)))));
            return updated.WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        if (node.AccessorList != null
            && TryLiftAccessorArrows(node.AccessorList, out var newAccessors))
        {
            return node.WithAccessorList(newAccessors)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitPropertyDeclaration(node);
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        // 声明级箭头索引器 T this[...] => r with {...}; → { get { ...; return clone; } }，
        // 下游 C12 IndexerDeclarationExpander 按 getAccessor.Body 原样携带。
        if (TryGetArrowSite(node.ExpressionBody, null,
                out var w, out var plan, out var site))
        {
            var updated = node
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithBody(BuildArrowBodyBlock(w, plan!, site!)))));
            return updated.WithTrailingTrivia(node.GetTrailingTrivia())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        if (node.AccessorList != null
            && TryLiftAccessorArrows(node.AccessorList, out var newAccessors))
        {
            return node.WithAccessorList(newAccessors)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        }
        return base.VisitIndexerDeclaration(node);
    }

    /// <summary>箭头体 with 站点识别：宿主无块体、箭头表达式剥括号后恰为可改写站点。
    /// 闸门站点/非 with 箭头（含 with 埋在实参/运算子内）一律 false，走默认游历。</summary>
    private bool TryGetArrowSite(
        ArrowExpressionClauseSyntax? arrow,
        BlockSyntax? blockBody,
        out WithExpressionSyntax w,
        out RecordClonePlan? plan,
        out WithSite? site)
    {
        if (arrow != null
            && blockBody == null
            && StripParens(arrow.Expression) is WithExpressionSyntax ww
            && TryGetRewritable(ww, out plan, out site))
        {
            w = ww;
            return true;
        }
        w = null!;
        plan = null;
        site = null;
        return false;
    }

    /// <summary>访问器列表内 get 箭头体逐个块化（set/init/add/remove 直接放 with 不合法，
    /// 分析期不认领）。只替换命中访问器，其余原样。</summary>
    private bool TryLiftAccessorArrows(
        AccessorListSyntax accessorList,
        out AccessorListSyntax rewritten)
    {
        var any = false;
        var accessors = new List<AccessorDeclarationSyntax>();
        foreach (var acc in accessorList.Accessors)
        {
            if (TryGetArrowSite(acc.ExpressionBody, acc.Body,
                    out var w, out var plan, out var site))
            {
                accessors.Add(acc
                    .WithExpressionBody(null)
                    .WithSemicolonToken(default)
                    .WithBody(BuildArrowBodyBlock(w, plan!, site!)));
                any = true;
            }
            else
            {
                // 未命中访问器（块体 set/get，或箭头体非 with 站点）必须继续默认游历，
                // 否则其中的语句位 with 会随红属性节点整体漏改。
                accessors.Add((AccessorDeclarationSyntax)base.VisitAccessorDeclaration(acc)!);
            }
        }
        rewritten = any
            ? SyntaxFactory.AccessorList(SyntaxFactory.List(accessors))
            : accessorList;
        return any;
    }

    /// <summary>箭头体提升块：复用 <see cref="BuildLiftedBlock"/> 的 wsrc/wclone 单次求值
    /// 与 set_X/字段直写发射，末尾 return 克隆体（箭头体在可编译输入中必为值返回位：
    /// void 成员直接放 with 触发 CS0201；return clone 与原箭头返回语义等价）。</summary>
    private BlockSyntax BuildArrowBodyBlock(
        WithExpressionSyntax w,
        RecordClonePlan plan,
        WithSite site)
    {
        var (block, cloneName) = BuildLiftedBlock(w, plan, site, tail: null);
        // return 与结果标识符间显式留白（同 VisitReturnStatement 的 token 融合教训）。
        var @return = SyntaxFactory.ReturnStatement(
            Identifier(cloneName).WithLeadingTrivia(SyntaxFactory.Space));
        return block.AddStatements(@return);
    }

    // ---- 表达式级：空初始化器内联（WR-3）；闸门站点挂跳过标注 ----

    public override SyntaxNode? VisitWithExpression(WithExpressionSyntax node)
    {
        if (!_siteMap.TryGetValue(node, out var hit))
        {
            // 回退豁免站点或分析期未登记的残余：豁免挂标注（先游历原始子节点再挂标，
            // 严禁 base.Visit(annotatedNode) 重入——那会在脱离树的红子树上按重置后的
            // 相对 span 游历，内部嵌套站点身份/span 全失配）；残余交 ReconcileResidual。
            if (_exemptSites.Contains(node))
                return AnnotateSkipped(node);
            return base.VisitWithExpression(node);
        }

        var (plan, site) = hit;
        if (plan.GateCode != null || !site.IsRewritable)
        {
            RegisterSkip(plan, site, node);
            // 先在原始在树节点上游历子女（嵌套 with 等逐站登记挂标），再给本节点挂跳过标。
            return AnnotateSkipped(node);
        }

        if (site.IsEmpty && !site.IsValueCopy)
        {
            // WR-3：class 空 with 纯克隆，单次求值，无语句。
            // struct/tuple 空 with 也要值拷贝局部（WR-4）——交给语句级提升，
            // 落到这里（不可提升位）由分类期 IsLiftableSite 闸门与 residual 兜底。
            Changed = true;
            _stats.WithSitesTotal++;
            _stats.WithSitesClass++;
            _stats.WithSitesEmpty++;
            if (_options.ReportInfo) ReportInfo(plan, site);
            return BuildEmptyCloneExpression(node.Expression, plan.FactoryName!);
        }

        // 非空站点由语句级提升接管；若意外落到这里（不可提升位），原样保留，
        // ReconcileResidual 以 5230 兜底，绝不产出半成品。
        return node;
    }

    /// <summary>
    /// 跳过族/回退豁免节点：先游历原始子女（嵌套站点身份与 span 必须落在原始在树节点上），
    /// 再装配子女并挂跳过标注。禁止对已挂标的脱离树红节点重跑 base.Visit——红子树脱离
    /// 语法树后内部 span 按子树根重置（实测嵌套 with span 错位），引用相等也失效。
    /// </summary>
    private SyntaxNode AnnotateSkipped(WithExpressionSyntax node)
    {
        var expression = (ExpressionSyntax?)Visit(node.Expression);
        var initializer = (InitializerExpressionSyntax?)Visit(node.Initializer);
        var updated = node;
        if (expression != null && !ReferenceEquals(expression, node.Expression))
            updated = updated.WithExpression(expression);
        if (initializer != null && !ReferenceEquals(initializer, node.Initializer))
            updated = updated.WithInitializer(initializer);
        return updated.WithAdditionalAnnotations(SkipAnnotation);
    }

    // ---- WR-8 residual 对账：输出树每个 with 必须已重写或挂 52xx 跳过标注 ----

    public void ReconcileResidual(SyntaxNode root)
    {
        foreach (var w in root.DescendantNodes().OfType<WithExpressionSyntax>())
        {
            if (w.HasAnnotations(SkipAnnotation.Kind)) continue;
            var span = w.GetLocation().GetLineSpan();
            _stats.ResidualWithCount++;
            _diagnostics.Add(new MemberDesugarDiagnostic(
                _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
                MemberDesugarDiagnosticCode.ResidualWith,
                RecordDiagnostics.ResidualWith(
                    span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1),
                _filePath,
                span.StartLinePosition.Line + 1,
                span.StartLinePosition.Character + 1,
                null));
        }
    }

    // ---- 提升序列构造 ----

    private (BlockSyntax Block, string CloneName) BuildLiftedBlock(
        WithExpressionSyntax w,
        RecordClonePlan plan,
        WithSite site,
        StatementSyntax? tail)
    {
        var stmts = new List<StatementSyntax>();

        // WR-5 接收者链（(a with {...}) with {...}）：先提升内层，外层源直接取内层克隆，
        // 内层源只求值一次，内层赋值整体先于外层克隆构造。
        var sourceExpression = w.Expression;
        if (StripParens(sourceExpression) is WithExpressionSyntax receiverInner
            && TryConsumeNestedWith(receiverInner, out var nestedStmts, out var nestedSource))
        {
            stmts.AddRange(nestedStmts);
            sourceExpression = nestedSource;
        }

        // WR-1/决策 10：简单接收者（标识符/this/内层克隆标识符）可直接重读零副作用观察；
        // 其余一律先提升，保证源接收者只求值一次（方法返回、属性、数组元素、对象创建等）。
        ExpressionSyntax source;
        if (IsSafeReceiver(sourceExpression))
        {
            source = sourceExpression;
        }
        else
        {
            var sourceName = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "wsrc");
            stmts.Add(DeclVar(sourceName, sourceExpression));
            _stats.TemporariesPromoted++;
            source = Identifier(sourceName);
        }

        var cloneName = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "wclone");
        ExpressionSyntax cloneInit;
        if (site.IsValueCopy)
        {
            // WR-4：struct/tuple 按值拷贝——克隆局部直接复制源局部，无工厂调用。
            cloneInit = source;
        }
        else
        {
            cloneInit = SyntaxFactory.InvocationExpression(
                FactoryAccess(source, plan.FactoryName!), SyntaxFactory.ArgumentList());
        }
        stmts.Add(DeclVar(cloneName, cloneInit));
        _stats.TemporariesPromoted++;
        var clone = Identifier(cloneName);

        foreach (var assignment in site.Assignments)
        {
            var rhs = assignment.Syntax.Right;

            // WR-5 右值链（a with { B = a.B with {...} }）：内层先提升，RHS 换内层克隆。
            if (StripParens(rhs) is WithExpressionSyntax rhsInner
                && TryConsumeNestedWith(rhsInner, out var rhsStmts, out var rhsReplacement))
            {
                stmts.AddRange(rhsStmts);
                rhs = rhsReplacement;
            }

            // WR-4 左值形态：位置 record struct 的合成位置属性经 reshape 终形走 set_X；
            // 普通 struct 属性/字段与元组 ItemN 对可变克隆局部一律直写。
            var usePositionalSetter = site.IsValueCopy
                && assignment is { IsProperty: true, Member: IPropertySymbol vp }
                && site.ReceiverType is { IsRecord: true }
                && PropertyFacts.IsPositionalRecordProperty(vp);

            ExpressionSyntax expression;
            if (assignment.IsProperty && (!site.IsValueCopy || usePositionalSetter))
            {
                expression = SyntaxFactory.InvocationExpression(
                    FactoryAccess(clone, AccessorsToMethods.SetterName(assignment.MemberName)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(rhs))));
                _stats.PropertyAssignments++;
            }
            else
            {
                expression = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    FactoryAccess(clone, assignment.MemberName),
                    rhs);
                _stats.FieldAssignments++;
            }
            stmts.Add(SyntaxFactory.ExpressionStatement(expression)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation));
        }

        if (tail != null) stmts.Add(tail);

        _stats.WithSitesTotal++;
        if (site.IsValueCopy)
        {
            _stats.WithSitesStruct++;
            if (site.IsEmpty) _stats.WithSitesEmpty++;
        }
        else
        {
            _stats.WithSitesClass++;
        }
        if (_options.ReportInfo) ReportInfo(plan, site);

        Changed = true;
        var block = SyntaxFactory.Block(stmts)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return (block, cloneName);
    }

    /// <summary>
    /// WR-5：消费直挂在接收者/RHS 位的内层 with。可行内层——class 空 with 内联为工厂
    /// 调用（零语句），其余递归构造内层提升块，RHS/源替换为内层克隆标识符；闸门/豁免
    /// 内层——整子树原子保留并给每个 with 挂跳过标注（防 residual 误报 5230）。
    /// </summary>
    private bool TryConsumeNestedWith(
        WithExpressionSyntax inner,
        out List<StatementSyntax> statements,
        out ExpressionSyntax replacement)
    {
        statements = new List<StatementSyntax>();
        if (_siteMap.TryGetValue(inner, out var hit))
        {
            var (nestedPlan, nestedSite) = hit;
            if (nestedPlan.GateCode != null || !nestedSite.IsRewritable)
            {
                replacement = (ExpressionSyntax)new NestedSkipAnnotator(this).Visit(inner)!;
                return true;
            }
            if (nestedSite.IsEmpty && !nestedSite.IsValueCopy)
            {
                Changed = true;
                _stats.WithSitesTotal++;
                _stats.WithSitesClass++;
                _stats.WithSitesEmpty++;
                if (_options.ReportInfo) ReportInfo(nestedPlan, nestedSite);
                replacement = BuildEmptyCloneExpression(inner.Expression, nestedPlan.FactoryName!);
                return true;
            }
            var (block, cloneName) = BuildLiftedBlock(inner, nestedPlan, nestedSite, tail: null);
            statements.AddRange(block.Statements);
            replacement = Identifier(cloneName);
            return true;
        }
        if (_exemptSites.Contains(inner))
        {
            replacement = (ExpressionSyntax)new NestedSkipAnnotator(this).Visit(inner)!;
            return true;
        }
        // 未登记的残余：原样返回，ReconcileResidual 以 5230 兜底。
        replacement = inner;
        return false;
    }

    /// <summary>WR-3 内联克隆表达式：<c>recv.Cs2jWithClone()</c>（复杂接收者加括号）。</summary>
    private static InvocationExpressionSyntax BuildEmptyCloneExpression(
        ExpressionSyntax receiver, string factoryName)
        => SyntaxFactory.InvocationExpression(
                FactoryAccess(MaybeParenthesize(receiver), factoryName),
                SyntaxFactory.ArgumentList())
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);

    /// <summary>
    /// 嵌套消费中遇到闸门/豁免子树：给子树内全部 with 站点登记跳过并挂 SkipAnnotation，
    /// 不做任何改写（原子保留）。只遍历挂标，不查跨度，红节点相对跨度问题不适用。
    /// </summary>
    private sealed class NestedSkipAnnotator : CSharpSyntaxRewriter
    {
        private readonly WithExpressionRewriter _owner;

        public NestedSkipAnnotator(WithExpressionRewriter owner) => _owner = owner;

        public override SyntaxNode? VisitWithExpression(WithExpressionSyntax node)
        {
            var visited = (WithExpressionSyntax?)base.VisitWithExpression(node);
            if (visited == null) return null;
            if (_owner._siteMap.TryGetValue(node, out var hit))
                _owner.RegisterSkip(hit.Plan, hit.Site, node);
            else if (!_owner._exemptSites.Contains(node))
                return visited;
            return visited.WithAdditionalAnnotations(SkipAnnotation);
        }
    }

    private void ReportInfo(RecordClonePlan plan, WithSite site)
    {
        _diagnostics.Add(new MemberDesugarDiagnostic(
            MemberDesugarSeverity.Info,
            MemberDesugarDiagnosticCode.WithSiteRewritten,
            RecordDiagnostics.WithSiteRewritten(
                plan.RecordType?.ToDisplayString() ?? site.ReceiverType?.ToDisplayString() ?? "?",
                site.IsEmpty, site.Assignments.Count, plan.FactoryName),
            _filePath, 0, 0, null));
    }

    private void RegisterSkip(RecordClonePlan plan, WithSite site, WithExpressionSyntax w)
    {
        var code = site.FailCode ?? plan.GateCode
            ?? MemberDesugarDiagnosticCode.NonRecordClassWith;
        var reason = site.FailReason ?? plan.GateReason
            ?? RecordDiagnostics.NonRecordClassWith(null, "unclassified with site");

        if (_skippedPlanIds.Add(plan.Id))
            _stats.PlansSkipped++;
        _stats.SkippedByReason.TryGetValue(code, out var count);
        _stats.SkippedByReason[code] = count + 1;

        var span = w.GetLocation().GetLineSpan();
        _diagnostics.Add(new MemberDesugarDiagnostic(
            _options.Strict ? MemberDesugarSeverity.Error : MemberDesugarSeverity.Warning,
            code, reason, _filePath,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            plan.RecordType?.Name));
    }

    private bool TryGetRewritable(
        WithExpressionSyntax w,
        out RecordClonePlan? plan,
        out WithSite? site)
    {
        if (_siteMap.TryGetValue(w, out var hit)
            && hit.Plan.GateCode == null && hit.Site.IsRewritable
            && (!hit.Site.IsEmpty || hit.Site.IsValueCopy))
        {
            plan = hit.Plan;
            site = hit.Site;
            return true;
        }
        plan = null;
        site = null;
        return false;
    }

    /// <summary>可安全重读的接收者：局部/参数标识符与 this（求值零可观察副作用）。
    /// base 不算——base.X() 为非虚调用，虚工厂必须经临时量保持虚分派。</summary>
    private static bool IsSafeReceiver(ExpressionSyntax receiver)
    {
        while (receiver is ParenthesizedExpressionSyntax parenthesized)
            receiver = parenthesized.Expression;
        return receiver is IdentifierNameSyntax or ThisExpressionSyntax;
    }

    private static ExpressionSyntax StripParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    /// <summary>复杂接收者成员访问前加括号（对象创建/转型/条件访问/二元等），
    /// 保证 <c>.Cs2jWithClone()</m> 绑定在整个接收者上且只求值一次。</summary>
    private static ExpressionSyntax MaybeParenthesize(ExpressionSyntax receiver)
    {
        receiver = StripParens(receiver);
        return receiver switch
        {
            IdentifierNameSyntax or ThisExpressionSyntax or BaseExpressionSyntax
                or MemberAccessExpressionSyntax or InvocationExpressionSyntax
                or ElementAccessExpressionSyntax or LiteralExpressionSyntax
                or DefaultExpressionSyntax or MemberBindingExpressionSyntax
                => receiver,
            _ => SyntaxFactory.ParenthesizedExpression(receiver),
        };
    }

    private static MemberAccessExpressionSyntax FactoryAccess(ExpressionSyntax receiver, string name)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            receiver,
            SyntaxFactory.Token(SyntaxKind.DotToken),
            SyntaxFactory.IdentifierName(name));

    private static IdentifierNameSyntax Identifier(string name)
        => SyntaxFactory.IdentifierName(name);

    private static LocalDeclarationStatementSyntax DeclVar(string name, ExpressionSyntax value)
        => SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                // "var" 与标识符间必须显式留白，避免无 trivia 相邻 token 融合。
                SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(name))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(value)))));

    /// <summary>with 节点引用相等比较器（同一编译单元内同语法节点唯一）。</summary>
    private sealed class SyntaxReferenceEqualityComparer : IEqualityComparer<WithExpressionSyntax>
    {
        public static readonly SyntaxReferenceEqualityComparer Instance = new();
        public bool Equals(WithExpressionSyntax? x, WithExpressionSyntax? y)
            => ReferenceEquals(x, y);
        public int GetHashCode(WithExpressionSyntax obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
