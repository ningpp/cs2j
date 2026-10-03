using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

        var factory = RecordDeclarationRewriter.TryBuildFactory(
            node, plan, _options, _stats, _diagnostics, _filePath);
        if (factory == null) return visited;
        Changed = true;
        return visited.AddMembers(factory);
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

        if (site.IsEmpty)
        {
            // WR-3：纯克隆，单次求值，无语句。
            Changed = true;
            _stats.WithSitesTotal++;
            _stats.WithSitesClass++;
            _stats.WithSitesEmpty++;
            if (_options.ReportInfo)
                ReportInfo(plan, site);
            return SyntaxFactory.InvocationExpression(
                    FactoryAccess(MaybeParenthesize(node.Expression), plan.FactoryName!),
                    SyntaxFactory.ArgumentList())
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
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

        // WR-1/决策 10：简单接收者（标识符/this）可直接重读零副作用观察；其余一律先提升，
        // 保证源接收者只求值一次（方法返回、属性、数组元素、对象创建等）。
        ExpressionSyntax source;
        if (IsSafeReceiver(w.Expression))
        {
            source = w.Expression;
        }
        else
        {
            var sourceName = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "wsrc");
            stmts.Add(DeclVar(sourceName, w.Expression));
            _stats.TemporariesPromoted++;
            source = Identifier(sourceName);
        }

        var cloneName = MemberDesugarSyntaxFacts.TempName(ref _tempCounter, "wclone");
        stmts.Add(DeclVar(cloneName, SyntaxFactory.InvocationExpression(
            FactoryAccess(source, plan.FactoryName!), SyntaxFactory.ArgumentList())));
        _stats.TemporariesPromoted++;
        var clone = Identifier(cloneName);

        foreach (var assignment in site.Assignments)
        {
            ExpressionSyntax expression;
            if (assignment.IsProperty)
            {
                expression = SyntaxFactory.InvocationExpression(
                    FactoryAccess(clone, AccessorsToMethods.SetterName(assignment.MemberName)),
                    SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(assignment.Syntax.Right))));
                _stats.PropertyAssignments++;
            }
            else
            {
                expression = SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    FactoryAccess(clone, assignment.MemberName),
                    assignment.Syntax.Right);
                _stats.FieldAssignments++;
            }
            stmts.Add(SyntaxFactory.ExpressionStatement(expression)
                .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation));
        }

        if (tail != null) stmts.Add(tail);

        _stats.WithSitesTotal++;
        _stats.WithSitesClass++;
        if (_options.ReportInfo) ReportInfo(plan, site);

        Changed = true;
        var block = SyntaxFactory.Block(stmts)
            .WithAdditionalAnnotations(MemberDesugarSyntaxFacts.RewriteAnnotation);
        return (block, cloneName);
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
            && hit.Plan.GateCode == null && hit.Site.IsRewritable && !hit.Site.IsEmpty)
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
