using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.AsyncNormalize.Await;

namespace CSharpToJava.Core.AsyncNormalize.Lambda;

/// <summary>
/// 按 <see cref="LambdaLiftPlan"/> 集执行外提与调用点改写（设计 §2 D3/D4、§4.2）。
/// 一个 rewriter 同时承担：
/// ① 计划内 async lambda 节点 → 显式委托构造 <c>new global::Delegate(method|method_w)</c>；
/// ② 计划内残余 async 局部函数声明移除、同类型内调用点改名（语义模型绑定防误改）；
/// ③ 提升方法/wrapper 成员按宿主类型插入（均挂 Cs2jAsyncNormalize annotation）；
/// ④ 非严格模式下 skip 节点（1802/1804/1807）原样保留并挂内存 skip 标注，
///    但其内部计划内的嵌套节点照常提升。
/// Prepare 先于根访问：提升方法体从原始子树经同一套替换规则构造，保证嵌套 lambda 的
/// 调用点在外提方法体内同步改写。
/// </summary>
internal sealed class AsyncLambdaLifter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly IReadOnlyList<LambdaLiftPlan> _plans;
    private readonly bool _annotateSkips;
    private readonly ConfigureAwaitStripper? _configureAwait;

    private readonly Dictionary<(int SpanStart, int RawKind), LambdaLiftPlan> _lambdaPlans = new();
    private readonly Dictionary<IMethodSymbol, LambdaLiftPlan> _localFunctionPlans =
        new(SymbolEqualityComparer.Default);
    // 键统一来自同一棵原始语法树的节点引用，默认引用相等即可。
    private readonly Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>> _membersToInsert = new();

    public bool Changed { get; private set; }

    public AsyncLambdaLifter(
        SemanticModel model,
        IReadOnlyList<LambdaLiftPlan> plans,
        bool annotateSkips,
        ConfigureAwaitStripper? configureAwait = null)
        : base(visitIntoStructuredTrivia: false)
    {
        _model = model;
        _plans = plans;
        _annotateSkips = annotateSkips;
        _configureAwait = configureAwait;

        foreach (var plan in plans)
        {
            if (!plan.IsLocalFunction)
                _lambdaPlans[(plan.Node.SpanStart, plan.Node.RawKind)] = plan;
            else if (model.GetDeclaredSymbol((LocalFunctionStatementSyntax)plan.Node) is IMethodSymbol symbol)
                _localFunctionPlans[symbol] = plan;
        }
    }

    /// <summary>构造全部提升方法/wrapper（体替换先走一遍），按宿主类型分桶待插入。</summary>
    public void Prepare()
    {
        var byHost = new Dictionary<TypeDeclarationSyntax, List<MemberDeclarationSyntax>>();

        foreach (var plan in _plans)
        {
            var members = new List<MemberDeclarationSyntax>();
            if (plan.NeedsWrapper)
                members.Add(BuildWrapper(plan));
            members.Add(BuildLiftedMethod(plan));

            if (!byHost.TryGetValue(plan.HostType, out var list))
            {
                list = new List<MemberDeclarationSyntax>();
                byHost[plan.HostType] = list;
            }
            list.AddRange(members);
            Changed = true;
        }

        foreach (var pair in byHost)
            _membersToInsert[pair.Key] = pair.Value;
    }

    // ── 类型成员插入 ──

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeCore(node, (TypeDeclarationSyntax?)base.VisitClassDeclaration(node));

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeCore(node, (TypeDeclarationSyntax?)base.VisitStructDeclaration(node));

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
        => VisitTypeCore(node, (TypeDeclarationSyntax?)base.VisitRecordDeclaration(node));

    private TypeDeclarationSyntax? VisitTypeCore(TypeDeclarationSyntax original, TypeDeclarationSyntax? visited)
    {
        if (visited == null)
            return null;
        if (!_membersToInsert.TryGetValue(original, out var toInsert))
            return visited;

        var members = visited.Members.ToList();
        foreach (var member in toInsert)
        {
            var withTrivia = member.WithLeadingTrivia(
                SyntaxFactory.TriviaList(
                    SyntaxFactory.CarriageReturnLineFeed,
                    SyntaxFactory.CarriageReturnLineFeed));
            members.Add(withTrivia);
        }

        return visited.WithMembers(SyntaxFactory.List(members));
    }

    // ── 调用点改写 ──

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        // 计划内 lambda：整棵替换为委托构造，绝不下访（体已在 Prepare 中经后序遍历造好；
        // 下访会重复剥离/计数并把结果丢弃）。
        if (_lambdaPlans.ContainsKey((node.SpanStart, node.RawKind)))
            return VisitLambdaCore(node, visitedNode: null);

        return VisitLambdaCore(node, base.VisitParenthesizedLambdaExpression(node));
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        if (_lambdaPlans.ContainsKey((node.SpanStart, node.RawKind)))
            return VisitLambdaCore(node, visitedNode: null);

        return VisitLambdaCore(node, base.VisitSimpleLambdaExpression(node));
    }

    private SyntaxNode? VisitLambdaCore(LambdaExpressionSyntax original, SyntaxNode? visitedNode)
    {
        if (_lambdaPlans.TryGetValue((original.SpanStart, original.RawKind), out var plan))
        {
            Changed = true;
            return BuildDelegateCreation(plan).WithTriviaFrom(original);
        }

        if (_annotateSkips && original.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
            && visitedNode is LambdaExpressionSyntax visitedLambda)
        {
            return visitedLambda.WithAdditionalAnnotations(
                new SyntaxAnnotation(AsyncNormalizeAnnotations.SkipAnnotationKind));
        }

        return visitedNode;
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        var visited = base.VisitAnonymousMethodExpression(node);
        if (_annotateSkips
            && node.Modifiers.Any(SyntaxKind.AsyncKeyword)
            && visited is AnonymousMethodExpressionSyntax visitedAnon)
        {
            return visitedAnon.WithAdditionalAnnotations(
                new SyntaxAnnotation(AsyncNormalizeAnnotations.SkipAnnotationKind));
        }
        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (IsPlannedLocalFunction(node, out _))
        {
            // 提升后声明从原位删除（体已整体外提）。
            Changed = true;
            return null;
        }

        var visited = base.VisitLocalFunctionStatement(node);
        if (_annotateSkips
            && node.Modifiers.Any(SyntaxKind.AsyncKeyword)
            && visited is LocalFunctionStatementSyntax visitedLocal)
        {
            return visitedLocal.WithAdditionalAnnotations(
                new SyntaxAnnotation(AsyncNormalizeAnnotations.SkipAnnotationKind));
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // 剥离判定必须在原始节点上做（本遍历从原始树起步，node 可被原 SemanticModel 绑定；
        // base 下访后重建的节点不再属于原树）。提升方法体内的 CA 站点经 Prepare 的
        // 原始子树遍历在此同步剥净。
        var stripConfigureAwait = _configureAwait?.ShouldStrip(_model, node) ?? false;

        var visited = (InvocationExpressionSyntax?)base.VisitInvocationExpression(node);
        if (visited == null)
            return null;

        if (TryGetPlannedLocalFunction(node, out var plan)
            && TryRewriteMethodGroupExpression(visited.Expression, plan, out var newExpression))
        {
            Changed = true;
            return visited.WithExpression(newExpression);
        }

        if (stripConfigureAwait
            && visited.Expression is MemberAccessExpressionSyntax visitedMemberAccess)
        {
            _configureAwait!.Record(node);
            return visitedMemberAccess.Expression.WithTriviaFrom(visited);
        }

        return visited;
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        // 成员访问右端（x.LocalFoo）不是调用点。
        if (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)
            return base.VisitIdentifierName(node);
        // 调用路径由 VisitInvocationExpression 统一处理。
        if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
            return base.VisitIdentifierName(node);
        if (node.Parent is NameColonSyntax)
            return base.VisitIdentifierName(node);

        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (TryResolveLocalFunction(symbol, out var plan))
        {
            // 方法组引用（赋值/传参/委托构造实参）。
            Changed = true;
            return BuildMethodGroup(plan).WithTriviaFrom(node);
        }

        return base.VisitIdentifierName(node);
    }

    private bool TryGetPlannedLocalFunction(InvocationExpressionSyntax node, out LambdaLiftPlan plan)
    {
        plan = null!;
        var symbol = _model.GetSymbolInfo(node).Symbol;
        return TryResolveLocalFunction(symbol, out plan);
    }

    private bool TryResolveLocalFunction(ISymbol? symbol, out LambdaLiftPlan plan)
    {
        plan = null!;
        if (symbol is not IMethodSymbol method)
            return false;
        return _localFunctionPlans.TryGetValue(method, out plan!)
            || _localFunctionPlans.TryGetValue(method.OriginalDefinition, out plan!);
    }

    private bool IsPlannedLocalFunction(LocalFunctionStatementSyntax node, out LambdaLiftPlan plan)
    {
        plan = null!;
        if (_model.GetDeclaredSymbol(node) is not IMethodSymbol symbol)
            return false;
        return _localFunctionPlans.TryGetValue(symbol, out plan!)
            || _localFunctionPlans.TryGetValue(symbol.OriginalDefinition, out plan!);
    }

    private bool TryRewriteMethodGroupExpression(
        ExpressionSyntax expression,
        LambdaLiftPlan plan,
        out ExpressionSyntax newExpression)
    {
        var rewritten = BuildMethodGroup(plan);
        newExpression = expression switch
        {
            IdentifierNameSyntax id => rewritten.WithTriviaFrom(id),
            // L<int>(...) 的泛型名整体替换（实参类型由新树推断）。
            GenericNameSyntax generic => rewritten.WithTriviaFrom(generic),
            _ => expression,
        };
        return newExpression != expression;
    }

    // ── 合成节点构造 ──

    private static ObjectCreationExpressionSyntax BuildDelegateCreation(LambdaLiftPlan plan)
    {
        var methodGroup = BuildMethodGroup(plan, plan.CallSiteMethodName);
        return SyntaxFactory.ObjectCreationExpression(
            SyntaxFactory.ParseTypeName(plan.DelegateTypeDisplay!),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(methodGroup))),
            initializer: null);
    }

    /// <summary>提升方法/包装方法的方法组表达式；泛型方法显式带类型参数，保证目标类型可推断失败时也可编译。</summary>
    private static ExpressionSyntax BuildMethodGroup(LambdaLiftPlan plan, string? methodNameOverride = null)
    {
        var name = methodNameOverride ?? plan.LiftedMethodName;
        if (plan.MethodTypeParameters.Count == 0)
            return SyntaxFactory.IdentifierName(name);

        return SyntaxFactory.GenericName(
            SyntaxFactory.Identifier(name),
            SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(
                plan.MethodTypeParameters.Select(static tp =>
                    (TypeSyntax)SyntaxFactory.IdentifierName(tp)))));
    }

    private MethodDeclarationSyntax BuildLiftedMethod(LambdaLiftPlan plan)
    {
        var modifiers = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        };
        if (plan.IsStatic)
            modifiers.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword));

        var parameters = plan.Parameters.Select(BuildParameterSyntax).ToList();
        var body = BuildMovedBody(plan);

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            SyntaxFactory.TokenList(modifiers),
            SyntaxFactory.ParseTypeName(plan.LiftedReturnTypeDisplay),
            explicitInterfaceSpecifier: null,
            SyntaxFactory.Identifier(plan.LiftedMethodName),
            BuildTypeParameterList(plan),
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
            SyntaxFactory.List(plan.ConstraintClauses),
            body,
            expressionBody: null);

        return method.WithAdditionalAnnotations(
            new SyntaxAnnotation(AsyncNormalizeAnnotations.AnnotationKind));
    }

    /// <summary>D3(c) 同步丢弃 wrapper：private[static] void &lt;name&gt;_w(同参) { 转发调用; }。</summary>
    private MethodDeclarationSyntax BuildWrapper(LambdaLiftPlan plan)
    {
        var parameters = plan.Parameters.Select(BuildParameterSyntax).ToList();

        var forwardArguments = plan.Parameters
            .Select(static p =>
            {
                var argument = SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Name));
                return p.RefKind switch
                {
                    RefKind.Ref => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
                    RefKind.Out => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
                    RefKind.In => argument.WithRefKindKeyword(SyntaxFactory.Token(SyntaxKind.InKeyword)),
                    RefKind.RefReadOnlyParameter => argument.WithRefKindKeyword(
                        SyntaxFactory.Token(SyntaxKind.InKeyword)),
                    _ => argument,
                };
            });

        var invocation = SyntaxFactory.InvocationExpression(
            BuildMethodGroup(plan, plan.LiftedMethodName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(forwardArguments)));

        var wrapperTokens = new List<SyntaxToken>
        {
            SyntaxFactory.Token(SyntaxKind.PrivateKeyword),
        };
        if (plan.IsStatic)
            wrapperTokens.Add(SyntaxFactory.Token(SyntaxKind.StaticKeyword));
        var modifiers = SyntaxFactory.TokenList(wrapperTokens);

        var method = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.List<AttributeListSyntax>(),
            modifiers,
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            explicitInterfaceSpecifier: null,
            SyntaxFactory.Identifier(plan.WrapperName!),
            BuildTypeParameterList(plan),
            SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(
                plan.Parameters.Select(BuildParameterSyntax))),
            SyntaxFactory.List(plan.ConstraintClauses),
            SyntaxFactory.Block(SyntaxFactory.ExpressionStatement(invocation)),
            expressionBody: null);

        return method.WithAdditionalAnnotations(
            new SyntaxAnnotation(AsyncNormalizeAnnotations.AnnotationKind));
    }

    private static TypeParameterListSyntax? BuildTypeParameterList(LambdaLiftPlan plan)
    {
        if (plan.MethodTypeParameters.Count == 0)
            return null;
        return SyntaxFactory.TypeParameterList(SyntaxFactory.SeparatedList(
            plan.MethodTypeParameters.Select(static name =>
                SyntaxFactory.TypeParameter(
                    SyntaxFactory.List<AttributeListSyntax>(),
                    default,
                    SyntaxFactory.Identifier(name)))));
    }

    private static ParameterSyntax BuildParameterSyntax(LambdaParameterPlan parameter)
    {
        var modifierList = parameter.RefKind switch
        {
            RefKind.Ref => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.RefKeyword)),
            RefKind.Out => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.OutKeyword)),
            RefKind.In => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            RefKind.RefReadOnlyParameter => SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.InKeyword)),
            _ => SyntaxFactory.TokenList(),
        };

        return SyntaxFactory.Parameter(
            SyntaxFactory.List<AttributeListSyntax>(),
            modifierList,
            SyntaxFactory.ParseTypeName(parameter.TypeDisplay),
            SyntaxFactory.Identifier(parameter.Name),
            @default: null);
    }

    /// <summary>
    /// 从原始体子树构造提升方法体：块体原样搬运（经嵌套替换）；表达式体按目标返回形态
    /// 转语句——void/非泛型 Task → 表达式语句；Task&lt;T&gt; → return 语句（保留原 await 语义）。
    /// </summary>
    private BlockSyntax BuildMovedBody(LambdaLiftPlan plan)
    {
        switch (plan.Node)
        {
            case ParenthesizedLambdaExpressionSyntax { Block: { } pBlock }:
                return (BlockSyntax)Visit(pBlock)!;
            case SimpleLambdaExpressionSyntax { Block: { } sBlock }:
                return (BlockSyntax)Visit(sBlock)!;
            case LocalFunctionStatementSyntax { Body: { } localBlock }:
                return (BlockSyntax)Visit(localBlock)!;

            case ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } pExpr }:
                return WrapExpressionBody(plan, (ExpressionSyntax)Visit(pExpr)!);
            case SimpleLambdaExpressionSyntax { ExpressionBody: { } sExpr }:
                return WrapExpressionBody(plan, (ExpressionSyntax)Visit(sExpr)!);
            case LocalFunctionStatementSyntax { ExpressionBody: { } localExpr }:
                return WrapExpressionBody(plan, (ExpressionSyntax)Visit(localExpr)!);

            default:
                throw new InvalidOperationException(
                    "C18: lift plan node has neither block nor expression body.");
        }
    }

    private BlockSyntax WrapExpressionBody(LambdaLiftPlan plan, ExpressionSyntax expression)
    {
        StatementSyntax statement = plan.ExpressionBodyNeedsReturn
            ? SyntaxFactory.ReturnStatement(expression)
            : SyntaxFactory.ExpressionStatement(expression);
        return SyntaxFactory.Block(statement);
    }
}
