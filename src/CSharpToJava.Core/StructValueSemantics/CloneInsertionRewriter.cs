using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// Phase 3：单点遍历完成「shim 注入 + VS-1/2/4 站点插 clone + VS-1c 临时提升」。
/// M3 覆盖 VS-1a/b/c、VS-2a/2b、VS-4a/b；VS-3/VS-5 与 SV040 族在 M4 补齐。
/// 站点判定顺序固定为 VS-5 → VS-3 → VS-4 → VS-2/VS-1（设计 §4.3），宁可少插不可错插。
/// 站点清单在分析期定型；本类只在「新鲜值/只读被调方/属性取值/自递归」不插条件成立时跳过。
/// </summary>
internal sealed partial class CloneInsertionRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly IReadOnlyList<ClonePlan> _activePlans;
    private readonly StructValueSemanticsAnalysis _analysis;
    private readonly StructValueSemanticsOptions _options;
    private readonly StructValueSemanticsStatistics _statistics;
    private readonly List<StructValueSemanticsDiagnostic> _diagnostics;
    private readonly string _filePath;
    private readonly ValueCopyFacts _facts;

    private readonly Dictionary<SyntaxAnnotation, ValueCopySite> _siteByAnnotation = new();
    private readonly HashSet<ValueCopySite> _applicableSites = new(ReferenceEqualityComparer.Instance);
    private int _chainedTempCounter;
    private int _defensiveTempCounter;

    // SV040-d：方法标注 →（持有者名，是否出口回写，是否 void）；路由标注 → 持有者名。
    private readonly Dictionary<SyntaxAnnotation, (string Holder, bool HasWrites, bool ReturnsVoid)>
        _captureByMethodAnnotation = new();
    private readonly Dictionary<SyntaxAnnotation, string> _holderByRouteAnnotation = new();
    private int _thisCaptureCounter;

    public CloneInsertionRewriter(
        SemanticModel model,
        IReadOnlyList<ClonePlan> activePlans,
        StructValueSemanticsAnalysis analysis,
        StructValueSemanticsOptions options,
        StructValueSemanticsStatistics statistics,
        List<StructValueSemanticsDiagnostic> diagnostics,
        string filePath)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
        _activePlans = activePlans;
        _analysis = analysis;
        _options = options;
        _statistics = statistics;
        _diagnostics = diagnostics;
        _filePath = filePath;
        _facts = new ValueCopyFacts(analysis.Compilation, analysis.PlansBySymbol);
    }

    /// <summary>
    /// 标注站点锚点 + this 捕获方法/路由节点 + 注入 shim，随后单点遍历改写。
    /// 所有「插/不插」「路由/不路由」裁决在本方法内基于原始树与原 SemanticModel
    /// 一次定案，遍历时只做形态变换。
    /// </summary>
    public CompilationUnitSyntax Rewrite(CompilationUnitSyntax root)
    {
        // 同一原始节点可能既是 clone 站点又是 this 路由节点 → 按节点聚合多个标注。
        var annotationsByNode =
            new Dictionary<SyntaxNode, List<SyntaxAnnotation>>(ReferenceEqualityComparer.Instance);

        void AddAnnotation(SyntaxNode node, SyntaxAnnotation annotation)
        {
            if (!annotationsByNode.TryGetValue(node, out var list))
            {
                list = new List<SyntaxAnnotation>();
                annotationsByNode[node] = list;
            }
            list.Add(annotation);
        }

        foreach (var plan in _activePlans)
        {
            foreach (var site in plan.Sites)
            {
                if (!string.Equals(site.FilePath, _filePath, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!IsApplicable(site)) continue;

                var annotation = new SyntaxAnnotation(StructValueSemanticsSyntaxFacts.SiteAnnotationKind);
                _siteByAnnotation[annotation] = site;
                _applicableSites.Add(site);
                AddAnnotation(site.Anchor, annotation);
            }

            AnnotateThisCaptures(plan, AddAnnotation);
        }

        // shim 注入点（主声明文件的类型声明）与站点锚点一次性标注/改写，
        // ReplaceNodes 支持嵌套节点（嵌套 struct、struct 声明内含站点）。
        var shimDeclarations = new Dictionary<TypeDeclarationSyntax, ClonePlan>();
        foreach (var plan in _activePlans)
        {
            if (!string.Equals(plan.PrimaryDeclarationFile, _filePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (plan.GeneratedCopyConstructor == null && plan.GeneratedCloneMethod == null)
                continue;
            if (FindPrimaryDeclaration(plan) is { } declaration)
                shimDeclarations[declaration] = plan;
        }

        var replaceableNodes = annotationsByNode.Keys
            .Concat(shimDeclarations.Keys.Cast<SyntaxNode>())
            .Distinct()
            .ToList();

        if (replaceableNodes.Count > 0)
        {
            root = root.ReplaceNodes(replaceableNodes, (original, partiallyRewritten) =>
            {
                if (original is TypeDeclarationSyntax originalDeclaration
                    && shimDeclarations.ContainsKey(originalDeclaration))
                {
                    return InjectShimMembers(
                        (TypeDeclarationSyntax)partiallyRewritten,
                        shimDeclarations[originalDeclaration]);
                }

                if (annotationsByNode.TryGetValue(original, out var annotations))
                    return partiallyRewritten.WithAdditionalAnnotations(annotations);

                return partiallyRewritten;
            });
        }

        return (CompilationUnitSyntax)Visit(root)!;
    }

    /// <summary>
    /// SV040-d：为本文件内需持有者改写的方法标注方法节点与方法体内全部 this 路由
    /// 节点（显式 <c>this</c> 与绑定到本 struct 的隐式 this 简单名）。裁决全部在
    /// 原始树 + 原模型上完成，与计划期 ThisCaptureFacts 口径一致。
    /// </summary>
    private void AnnotateThisCaptures(
        ClonePlan plan,
        Action<SyntaxNode, SyntaxAnnotation> addAnnotation)
    {
        foreach (var capture in plan.ThisCaptureRewrites)
        {
            foreach (var reference in capture.Method.DeclaringSyntaxReferences)
            {
                if (!string.Equals(reference.SyntaxTree.FilePath, _filePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (reference.GetSyntax() is not MethodDeclarationSyntax method) continue;

                var scope = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                if (scope == null) continue;

                var holder = StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix
                    + _thisCaptureCounter++.ToString();
                var methodAnnotation =
                    new SyntaxAnnotation(StructValueSemanticsSyntaxFacts.ThisCaptureMethodAnnotationKind);
                _captureByMethodAnnotation[methodAnnotation] =
                    (holder, capture.HasWrites, capture.Method.ReturnsVoid);
                addAnnotation(method, methodAnnotation);

                foreach (var descendant in scope.DescendantNodes())
                {
                    if (descendant is ThisExpressionSyntax)
                    {
                        var route = new SyntaxAnnotation(
                            StructValueSemanticsSyntaxFacts.ThisCaptureRouteAnnotationKind);
                        _holderByRouteAnnotation[route] = holder;
                        addAnnotation(descendant, route);
                    }
                    else if (descendant is SimpleNameSyntax simple
                        && ThisCaptureFacts.IsImplicitThisMember(simple, plan.Symbol, _model))
                    {
                        var route = new SyntaxAnnotation(
                            StructValueSemanticsSyntaxFacts.ThisCaptureRouteAnnotationKind);
                        _holderByRouteAnnotation[route] = holder;
                        addAnnotation(descendant, route);
                    }
                }
            }
        }
    }

    // ── 站点适用性裁决（原始树 / 原模型；设计 §4.3 不插条件列） ──

    private bool IsApplicable(ValueCopySite site)
    {
        switch (site.Kind)
        {
            case SiteKind.LocalInit:
            case SiteKind.Assign:
            case SiteKind.ArrayStore:
                return IsValueExpressionApplicable(site);

            case SiteKind.Return:
                if (!IsValueExpressionApplicable(site)) return false;
                return !IsCloneMethodSelfReturn(site);

            case SiteKind.Argument:
                return IsArgumentApplicable(site);

            case SiteKind.ChainedAssign:
                // 表达式位置（if 条件/实参/字段初始化器等）无法提升为语句 → SV043。
                if (site.IsStatementPosition) return true;
                ReportCannotHoist(site, "chained struct assignment occurs in an expression position " +
                    "where a temporary cannot be hoisted.");
                return false;

            case SiteKind.Boxing:
            case SiteKind.Interface:
                // 新鲜值装箱不共享存储（new 出的对象即箱本体），无需 clone。
                return !ValueCopyFacts.IsFreshValue((ExpressionSyntax)site.Anchor);

            case SiteKind.Unbox:
                // C# 拆箱产生副本；Java 侧箱与值同引用，消费点必须 clone（无新鲜值豁免）。
                return true;

            case SiteKind.DefensiveReceiver:
                if (site.IsStatementPosition) return true;
                ReportCannotHoist(site, "a mutating method is called on a defensive-copy receiver in an " +
                    "expression position where a temporary cannot be hoisted.");
                return false;

            default:
                return false;
        }
    }

    private bool IsValueExpressionApplicable(ValueCopySite site)
    {
        var expression = (ExpressionSyntax)site.Anchor;
        // 新鲜值（new/调用/运算/default/字面量）本身就是独立值，无需再 clone。
        return !ValueCopyFacts.IsFreshValue(expression);
    }

    /// <summary>VS-2a 特例：struct 自己的零参 Clone() 内 <c>return this;</c> 不得包 Clone()（自递归）。</summary>
    private bool IsCloneMethodSelfReturn(ValueCopySite site)
    {
        var enclosing = _model.GetEnclosingSymbol(site.Anchor.SpanStart) as IMethodSymbol;
        if (enclosing == null || enclosing.IsStatic) return false;
        if (enclosing.Name != CloneShimGenerator.CloneMethodName || enclosing.Parameters.Length != 0)
            return false;
        return SymbolEqualityComparer.Default.Equals(
            enclosing.ReturnType.OriginalDefinition, site.Plan.Symbol.OriginalDefinition);
    }

    private bool IsArgumentApplicable(ValueCopySite site)
    {
        if (!_options.InsertAtValueArguments) return false;

        var expression = (ExpressionSyntax)site.Anchor;
        if (ValueCopyFacts.IsFreshValue(expression)) return false;
        // 属性取值：getter 侧（转换器期）已返回独立副本，调用点不重复 clone；
        // 数组元素读取刻意不在此列（VS-4b/W4：必插，且优先于只读形参豁免）。
        if (ValueCopyFacts.IsPropertyAccess(expression, _model)) return false;

        if (expression.Parent is not ArgumentSyntax argument) return false;
        var parameter = ValueCopyFacts.ResolveArgumentParameter(argument, _model);
        if (parameter == null) return false; // 未绑定/params 展开：宁可少插
        if (parameter.RefKind != RefKind.None) return false;

        // VS-4b：C# 数组元素读出即副本，无论被调方是否只读都必须在消费点插（W4 回归）。
        if (ValueCopyFacts.IsArrayElementLoad(expression, _model)) return true;

        // 被调方只读分析通过 → 形参副本不会被变异/转发，调用点无需 clone。
        if (_facts.IsValueParameterEffectivelyReadOnly(parameter)) return false;

        return true;
    }

    // ── 遍历：表达式站点包裹 + VS-1c 语句提升 ──

    public override SyntaxNode? Visit(SyntaxNode? node)
    {
        var visited = base.Visit(node);
        if (visited == null) return null;

        var site = FindApplicableSite(visited);
        if (site == null
            || site.Kind == SiteKind.ChainedAssign
            || site.Kind == SiteKind.DefensiveReceiver)
        {
            return visited; // 语句级提升站点在 VisitExpressionStatement 处理
        }

        return ApplyExpressionSite(site, visited);
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        var visited = base.VisitExpressionStatement(node);
        if (visited is not ExpressionStatementSyntax expressionStatement) return visited;

        var site = FindApplicableSite(expressionStatement.Expression);
        if (site is { Kind: SiteKind.ChainedAssign })
            return HoistChainedAssignment(site, expressionStatement);
        if (site is { Kind: SiteKind.DefensiveReceiver })
            return HoistDefensiveReceiver(site, expressionStatement);

        return visited;
    }

    // ── SV040-d：this 路由 + 方法级 copy-in/copy-out 持有者改写 ──

    public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node)
    {
        var visited = base.VisitThisExpression(node);
        if (visited is not ThisExpressionSyntax visitedThis) return visited;
        if (!TryGetRouteHolder(node, out var holder)) return visitedThis;

        // 保留原节点全部标注（可能同时是 clone 站点锚点），供外层 Visit 继续包裹。
        var replacement = SyntaxFactory.IdentifierName(holder).WithTriviaFrom(visitedThis);
        return visitedThis.CopyAnnotationsTo(replacement);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var visited = (SimpleNameSyntax)base.VisitIdentifierName(node)!;
        return RouteSimpleName(node, visited);
    }

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (SimpleNameSyntax)base.VisitGenericName(node)!;
        return RouteSimpleName(node, visited);
    }

    private SyntaxNode RouteSimpleName(SimpleNameSyntax original, SimpleNameSyntax visited)
    {
        if (!TryGetRouteHolder(original, out var holder)) return visited;

        var memberAccess = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(holder),
                visited.WithoutTrivia())
            .WithTriviaFrom(visited);
        return visited.CopyAnnotationsTo(memberAccess);
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        var methodAnnotation = node
            .GetAnnotations(StructValueSemanticsSyntaxFacts.ThisCaptureMethodAnnotationKind)
            .FirstOrDefault();
        if (methodAnnotation == null
            || !_captureByMethodAnnotation.TryGetValue(methodAnnotation, out var capture))
        {
            return visited;
        }

        var (holder, hasWrites, returnsVoid) = capture;
        var holderDeclaration = CreateHolderDeclaration(holder);

        BlockSyntax newBody;
        if (visited.Body is { } body)
        {
            newBody = BuildHolderBody(
                body.OpenBraceToken, body.CloseBraceToken,
                holderDeclaration, body.Statements, holder, hasWrites);
            visited = visited.WithBody(newBody)
                .WithExpressionBody(null)
                .WithSemicolonToken(default);
        }
        else if (visited.ExpressionBody is { } expressionBody)
        {
            StatementSyntax effectiveStatement = returnsVoid
                ? SyntaxFactory.ExpressionStatement(expressionBody.Expression)
                : SyntaxFactory.ReturnStatement(expressionBody.Expression);
            newBody = BuildHolderBody(
                SyntaxFactory.Token(SyntaxKind.OpenBraceToken),
                SyntaxFactory.Token(SyntaxKind.CloseBraceToken),
                holderDeclaration,
                SyntaxFactory.SingletonList(effectiveStatement),
                holder, hasWrites);
            visited = visited.WithExpressionBody(null)
                .WithBody(newBody)
                .WithSemicolonToken(default);
        }
        else
        {
            return visited;
        }

        _statistics.ThisCaptureMethodsRewritten++;
        if (!hasWrites) _statistics.ThisCaptureReadOnlyMethods++;

        return visited.WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);
    }

    /// <summary><c>var holder = this;</c>（copy-in；持有者声明本身不插 clone）。</summary>
    private static LocalDeclarationStatementSyntax CreateHolderDeclaration(string holder) =>
        SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(holder))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            SyntaxFactory.ThisExpression())))));

    /// <summary>
    /// 组装方法新体：首部 <c>var holder = this;</c>；有写时原语句整体包
    /// <c>try { ... } finally { this = holder; }</c>（copy-out 保证所有出口回写），
    /// 纯读时不包 try/finally（只读快照语义等同 async/iterator 的按值快照）。
    /// </summary>
    private static BlockSyntax BuildHolderBody(
        SyntaxToken openBrace,
        SyntaxToken closeBrace,
        LocalDeclarationStatementSyntax holderDeclaration,
        SyntaxList<StatementSyntax> originalStatements,
        string holder,
        bool hasWrites)
    {
        var statements = SyntaxFactory.List<StatementSyntax>(
            new[] { holderDeclaration.WithAdditionalAnnotations(
                StructValueSemanticsSyntaxFacts.RewriteAnnotation) });

        if (hasWrites)
        {
            var copyOut = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    SyntaxFactory.ThisExpression(),
                    SyntaxFactory.IdentifierName(holder)));
            var tryFinally = SyntaxFactory.TryStatement(
                SyntaxFactory.List<AttributeListSyntax>(),
                SyntaxFactory.Token(SyntaxKind.TryKeyword),
                SyntaxFactory.Block(originalStatements),
                SyntaxFactory.List<CatchClauseSyntax>(),
                SyntaxFactory.FinallyClause(
                    SyntaxFactory.Token(SyntaxKind.FinallyKeyword),
                    SyntaxFactory.Block(copyOut)));
            statements = statements.Add(tryFinally
                .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation));
        }
        else
        {
            statements = statements.AddRange(originalStatements);
        }

        return SyntaxFactory.Block(openBrace, statements, closeBrace)
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);
    }

    private bool TryGetRouteHolder(SyntaxNode node, out string holder)
    {
        foreach (var annotation in node.GetAnnotations(
                     StructValueSemanticsSyntaxFacts.ThisCaptureRouteAnnotationKind))
        {
            if (_holderByRouteAnnotation.TryGetValue(annotation, out var name))
            {
                holder = name;
                return true;
            }
        }
        holder = string.Empty;
        return false;
    }

    private void ReportCannotHoist(ValueCopySite site, string reason)
    {
        var location = site.Anchor.GetLocation();
        var span = location.GetLineSpan().StartLinePosition;
        _diagnostics.Add(new StructValueSemanticsDiagnostic(
            StructValueSemanticsSeverity.Error,
            StructValueSemanticsDiagnosticCode.SV043,
            reason,
            site.FilePath,
            span.Line + 1, span.Character + 1,
            site.Plan.StructQualifiedName));
    }

    private SyntaxNode ApplyExpressionSite(ValueCopySite site, SyntaxNode visited)
    {
        var expression = (ExpressionSyntax)visited;
        var wrapped = ValueCopyFacts.WrapClone(expression)
            .WithLeadingTrivia(expression.GetLeadingTrivia())
            .WithTrailingTrivia(expression.GetTrailingTrivia())
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);

        CountExpressionSite(site, (ExpressionSyntax)site.Anchor);
        return wrapped;
    }

    private void CountExpressionSite(ValueCopySite site, ExpressionSyntax original)
    {
        switch (site.Kind)
        {
            case SiteKind.LocalInit: _statistics.LocalInitSites++; break;
            case SiteKind.Assign: _statistics.AssignSites++; break;
            case SiteKind.Return: _statistics.ReturnSites++; break;
            case SiteKind.Argument: _statistics.ArgumentSites++; break;
            case SiteKind.ArrayStore: _statistics.ArrayStoreSites++; break;
            case SiteKind.Boxing: _statistics.BoxingSites++; break;
            case SiteKind.Interface: _statistics.InterfaceSites++; break;
            case SiteKind.Unbox: _statistics.UnboxSites++; break;
        }

        // VS-4b：struct 数组元素读取流入消费点，单点包裹时额外计入元素读取。
        if (ValueCopyFacts.IsArrayElementLoad(original, _model))
            _statistics.ArrayLoadSites++;

        _statistics.SitesRewritten++;
    }

    /// <summary>
    /// VS-1c：<c>a = b = e;</c> → <c>var __cs2jSc0 = e; b = __cs2jSc0.Clone();
    /// a = __cs2jSc0.Clone();</c>。目标按逆序赋值，保证两个槽互不别名。
    /// </summary>
    private StatementSyntax HoistChainedAssignment(
        ValueCopySite site, ExpressionStatementSyntax statement)
    {
        var assignment = (AssignmentExpressionSyntax)statement.Expression;

        var targets = new List<ExpressionSyntax>();
        var current = (ExpressionSyntax)assignment;
        while (current is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } chain)
        {
            targets.Add(chain.Left);
            current = chain.Right;
        }
        var sourceValue = current;

        var tempName = StructValueSemanticsSyntaxFacts.ChainedAssignTempPrefix
            + _chainedTempCounter++.ToString();

        var tempDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(tempName))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(sourceValue.WithoutTrivia())))));

        var statements = new List<StatementSyntax> { tempDeclaration };
        for (var i = targets.Count - 1; i >= 0; i--)
        {
            var cloneCall = ValueCopyFacts.WrapClone(SyntaxFactory.IdentifierName(tempName));
            var assignmentStatement = SyntaxFactory.ExpressionStatement(
                SyntaxFactory.AssignmentExpression(
                    SyntaxKind.SimpleAssignmentExpression,
                    targets[i].WithoutTrivia(),
                    cloneCall));
            statements.Add(assignmentStatement
                .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation));
        }

        _statistics.ChainedAssignSites++;
        _statistics.SitesRewritten++;

        return SyntaxFactory.Block(statements)
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia())
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);
    }

    /// <summary>
    /// VS-3：<c>r.Mutate();</c>（r 为 in 参/readonly 字段/get-only 属性/foreach 变量/rvalue）→
    /// <c>var __cs2jVc0 = r.Clone(); __cs2jVc0.Mutate();</c>（设计 §4.3）。
    /// </summary>
    private StatementSyntax HoistDefensiveReceiver(
        ValueCopySite site, ExpressionStatementSyntax statement)
    {
        var invocation = (InvocationExpressionSyntax)statement.Expression;
        var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;

        var tempName = StructValueSemanticsSyntaxFacts.DefensiveCopyTempPrefix
            + _defensiveTempCounter++.ToString();

        var tempDeclaration = SyntaxFactory.LocalDeclarationStatement(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.IdentifierName("var"),
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(tempName))
                        .WithInitializer(SyntaxFactory.EqualsValueClause(
                            ValueCopyFacts.WrapClone(memberAccess.Expression.WithoutTrivia()))))));

        var callOnTemp = SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(tempName),
                    memberAccess.Name.WithoutTrivia()),
                invocation.ArgumentList.WithoutTrivia()))
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);

        _statistics.DefensiveReceiverSites++;
        _statistics.SitesRewritten++;

        return SyntaxFactory.Block(tempDeclaration, callOnTemp)
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia())
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);
    }

    private ValueCopySite? FindApplicableSite(SyntaxNode node)
    {
        foreach (var annotation in node.GetAnnotations(StructValueSemanticsSyntaxFacts.SiteAnnotationKind))
        {
            if (_siteByAnnotation.TryGetValue(annotation, out var site)
                && _applicableSites.Contains(site))
            {
                return site;
            }
        }
        return null;
    }

    // ── shim 注入 ──

    private TypeDeclarationSyntax? FindPrimaryDeclaration(ClonePlan plan)
    {
        foreach (var reference in plan.Symbol.DeclaringSyntaxReferences)
        {
            if (!string.Equals(reference.SyntaxTree.FilePath, _filePath, StringComparison.OrdinalIgnoreCase))
                continue;
            if (reference.GetSyntax().FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } declaration)
                return declaration;
        }
        return null;
    }

    private static TypeDeclarationSyntax InjectShimMembers(TypeDeclarationSyntax declaration, ClonePlan plan)
    {
        var members = new List<MemberDeclarationSyntax>();
        if (plan.GeneratedCopyConstructor != null)
            members.Add(plan.GeneratedCopyConstructor
                .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation));
        if (plan.GeneratedCloneMethod != null)
            members.Add(plan.GeneratedCloneMethod
                .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation));

        return declaration.AddMembers(members.ToArray())
            .WithAdditionalAnnotations(StructValueSemanticsSyntaxFacts.RewriteAnnotation);
    }
}
