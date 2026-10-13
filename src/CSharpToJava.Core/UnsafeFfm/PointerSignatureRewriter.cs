using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.UnsafeFfm;

/// <summary>
/// R5：指针签名白名单改写（设计 D6，§4 组件表；M3a 第一部分）。
/// 全部判定基于 SemanticModel/ISymbol，语法形态由临时 probe（Roslyn 5.0.0，
/// LanguageVersion.Latest）实测确认：
/// <list type="bullet">
/// <item>基元/可封送元素指针形参——byte/sbyte/short/ushort/int/uint/long/
/// ulong/char/float/double/bool（共 12 个 SpecialType）——归一为 <c>T[]</c>；
/// 相邻「指针+长度」参数模式同样按数组（长度旁证仅计数）；</item>
/// <item>指针返回类型归一为 <c>T[]</c>（如 <c>byte* M()</c>→<c>byte[] M()</c>）；</item>
/// <item>指针局部变量仅当其初值为「R5 已改写形参/局部」或「R2 已改写
/// fixed 字段」的标识符时，改写为数组引用并继承流向；其它初值形态留给
/// R3 既有模型处理（R3 域内成功或 UF020）；</item>
/// <item>白名单边界：<c>void*</c>、<c>delegate*</c>（FunctionPointerTypeSyntax/
/// IFunctionPointerTypeSymbol）、泛型指针（元素为类型参数）、多级指针、
/// enum/IntPtr 指针、非基元 struct 指针形参 → UF021；指针字段/属性/事件
/// （指针存储位，不可证）→ UF020；</item>
/// <item>fixed 语句整棵子树不进入（R3 域）。</item>
/// </list>
/// 被改写符号的使用点（*p/p[i] 等）不在本规则处理，由 R6
/// <see cref="PointerExprRewriter"/> 消费同一 <see cref="PointerFlowModel"/>。
/// 产物签名均为数组形态，本规则天然幂等（二次输入无指针符号）。
/// </summary>
internal sealed class PointerSignatureRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _model;
    private readonly HashSet<IFieldSymbol> _rewrittenFields;
    private readonly UnsafeFfmStatistics _statistics;
    private readonly OriginalSyntaxResolver _resolver;
    private readonly PointerFlowModel _flowModel;
    private readonly PointerMethodCatalog _catalog;

    /// <summary>当前正在访问体的配对方法计划（栈：支持嵌套局部函数）。</summary>
    private readonly List<PairedMethodPlan> _pairedStack = new();

    public PointerSignatureRewriter(
        SemanticModel model,
        HashSet<IFieldSymbol> rewrittenFields,
        PointerFlowModel flowModel,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver,
        PointerMethodCatalog catalog)
    {
        _model = model;
        _rewrittenFields = rewrittenFields;
        _flowModel = flowModel;
        _statistics = statistics;
        _resolver = resolver;
        _catalog = catalog;
    }

    /// <summary>已改写为数组的指针符号（形参+局部），供 R3 逃逸检查/Classify 使用。</summary>
    public HashSet<ISymbol> RewrittenSymbols { get; } =
        new(SymbolEqualityComparer.Default);

    public PointerFlowModel FlowModel => _flowModel;

    public List<UnsafeFfmDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors =>
        Diagnostics.Any(d => d.Severity == UnsafeFfmSeverity.Error);

    // ---- fixed 语句：整棵跳过（R3 域）----

    public override SyntaxNode? VisitFixedStatement(FixedStatementSyntax node)
        // 不 Visit 子树：R5 不触碰 fixed 变量与块体，全部交 R3。
        => node;

    // ---- 方法 / 局部函数 / 运算符 ----

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (GetCurrentPlan(node) is { } paired)
            return ApplyPairedMethod(node, paired);

        var parameters = node.ParameterList.Parameters;
        var parameterRewrites = PlanParameterRewrites(parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.ReturnType);

        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        return ApplyMethodSignature(visited, parameterRewrites, returnPlan);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        if (GetCurrentPlan(node) is { } paired)
        {
            var local = (LocalFunctionStatementSyntax)VisitPairedBody(
                paired, () => base.VisitLocalFunctionStatement(node)!);
            var pairedUpdated = local.WithParameterList(
                ApplyPairedParameters(local.ParameterList, paired));
            var pairedResult = paired.ReturnsPointer
                ? pairedUpdated.WithReturnType(PredefinedType(Token(SyntaxKind.IntKeyword)))
                : pairedUpdated;
            return _catalog.AnnotatePaired(pairedResult, paired.Method);
        }

        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.ReturnType);

        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;

        var updated = visited.WithParameterList(ApplyParameterRewrites(
            visited.ParameterList, parameterRewrites));
        if (returnPlan is TypeSyntaxRewrite plan)
            updated = updated.WithReturnType(BuildArrayType(plan.Pointer));

        return updated;
    }

    /// <summary>
    /// 取节点对应方法/局部函数符号的配对计划；非配对返回 null（走既有
    /// array-only 路径）。
    /// </summary>
    private PairedMethodPlan? GetCurrentPlan(SyntaxNode node)
    {
        var original = _resolver.Resolve(node);
        var symbol = original == null
            ? null
            : _model.GetDeclaredSymbol(original) as IMethodSymbol;
        return symbol == null ? null : _catalog.GetPlan(symbol);
    }

    private MethodDeclarationSyntax ApplyPairedMethod(
        MethodDeclarationSyntax node, PairedMethodPlan plan)
    {
        var visited = (MethodDeclarationSyntax)VisitPairedBody(
            plan, () => base.VisitMethodDeclaration(node)!);
        var updated = visited
            .WithParameterList(ApplyPairedParameters(visited.ParameterList, plan));
        var result = plan.ReturnsPointer
            ? updated.WithReturnType(PredefinedType(Token(SyntaxKind.IntKeyword)))
            : updated;
        return _catalog.AnnotatePaired(result, plan.Method);
    }

    /// <summary>
    /// 在压入配对计划、注册形参 flow 的前提下访问方法体。注册必须先于体访问：
    /// shadow copy-in/形参别名局部要继承这些 flow。
    /// </summary>
    private SyntaxNode VisitPairedBody(
        PairedMethodPlan plan,
        Func<SyntaxNode> visit)
    {
        foreach (var parameter in plan.Parameters)
        {
            if (parameter.Symbol == null) continue;

            if (parameter.Kind == PairedParamKind.PlainPointer)
            {
                RewrittenSymbols.Add(parameter.Symbol);
                _statistics.PointerParametersRewritten++;
                _flowModel.Register(parameter.Symbol, new PointerFlow(
                    PointerSourceKind.ParamArray,
                    parameter.ArrayName,
                    parameter.IndexName)
                {
                    FoldsToOffset = true,
                });
            }
            else if (parameter.Kind == PairedParamKind.HolderPointer)
            {
                RewrittenSymbols.Add(parameter.Symbol);
            }
        }

        if (plan.ReturnsPointer)
            _statistics.PointerReturnsRewritten++;

        _pairedStack.Add(plan);
        try
        {
            return visit();
        }
        finally
        {
            _pairedStack.RemoveAt(_pairedStack.Count - 1);
        }
    }

    /// <summary>
    /// 按配对计划重建形参表：指针形参 → 数组形参 + 紧随的 int 索引形参；
    /// holder 指针形参 → ByRef&lt;int&gt; + 紧随的伴生 T[] 数组形参。
    /// </summary>
    private ParameterListSyntax ApplyPairedParameters(
        ParameterListSyntax visitedList, PairedMethodPlan plan)
    {
        var result = new List<ParameterSyntax>();
        for (var i = 0; i < plan.Parameters.Count; i++)
        {
            var parameterPlan = plan.Parameters[i];
            var visitedParameter = visitedList.Parameters[i];

            if (parameterPlan.Kind == PairedParamKind.None)
            {
                result.Add(visitedParameter);
                continue;
            }

            var elementSyntax = parameterPlan.Kind == PairedParamKind.PlainPointer
                ? ArrayElementTypeSyntax(visitedParameter)
                : HolderPointerElementTypeSyntax(visitedParameter);

            if (parameterPlan.Kind == PairedParamKind.PlainPointer)
            {
                result.Add(visitedParameter.WithType(BuildArrayTypeFromElement(elementSyntax)));
                result.Add(Parameter(Identifier(parameterPlan.IndexName))
                    .WithType(PredefinedType(Token(SyntaxKind.IntKeyword))));
            }
            else
            {
                result.Add(visitedParameter.WithType(RebuildHolderIntType(visitedParameter.Type)));
                result.Add(Parameter(Identifier(parameterPlan.ArrayName))
                    .WithType(BuildArrayTypeFromElement(elementSyntax)));
            }
        }

        return visitedList.WithParameters(SeparatedList(result));
    }

    /// <summary>数组-only 路径形参语法上的指针元素（T* p 的 T）。</summary>
    private static TypeSyntax ArrayElementTypeSyntax(ParameterSyntax parameter)
        => ((PointerTypeSyntax)parameter.Type).ElementType;

    /// <summary>holder 形参语法 ByRef&lt;T*&gt; 上的指针元素 T。</summary>
    private static TypeSyntax HolderPointerElementTypeSyntax(ParameterSyntax parameter)
    {
        var generic = (GenericNameSyntax)parameter.Type;
        return ((PointerTypeSyntax)generic.TypeArgumentList.Arguments[0]).ElementType;
    }

    private static GenericNameSyntax RebuildHolderIntType(TypeSyntax holderType)
    {
        var generic = (GenericNameSyntax)holderType;
        return generic.WithTypeArgumentList(TypeArgumentList(
            SingletonSeparatedList<TypeSyntax>(
                PredefinedType(Token(SyntaxKind.IntKeyword)))));
    }

    private static ArrayTypeSyntax BuildArrayTypeFromElement(TypeSyntax element)
        => ArrayType(element)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));


    public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node)
    {
        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.ReturnType);

        var visited = (OperatorDeclarationSyntax)base.VisitOperatorDeclaration(node)!;

        var updated = visited.WithParameterList(ApplyParameterRewrites(
            visited.ParameterList, parameterRewrites));
        if (returnPlan is TypeSyntaxRewrite plan)
            updated = updated.WithReturnType(BuildArrayType(plan.Pointer));

        return updated;
    }

    public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node)
    {
        // 转换运算符只有「目标类型」（如 operator byte*(...)），无返回类型写法。
        var targetPlan = PlanTypeRewrite(node.Type);
        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);

        var visited = (ConversionOperatorDeclarationSyntax)
            base.VisitConversionOperatorDeclaration(node)!;

        var updated = visited.WithParameterList(ApplyParameterRewrites(
            visited.ParameterList, parameterRewrites));
        if (targetPlan is TypeSyntaxRewrite plan)
            updated = updated.WithType(BuildArrayType(plan.Pointer));

        return updated;
    }

    // ---- 委托 / 构造 / 索引器 ----

    public override SyntaxNode? VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.ReturnType);

        var updated = node.WithParameterList(ApplyParameterRewrites(
            node.ParameterList, parameterRewrites));
        if (returnPlan is TypeSyntaxRewrite plan)
            updated = updated.WithReturnType(BuildArrayType(plan.Pointer));

        return updated;
    }

    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);

        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
        return visited.WithParameterList(ApplyParameterRewrites(
            visited.ParameterList, parameterRewrites));
    }

    public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
    {
        var parameterRewrites = PlanParameterRewrites(
            node.ParameterList.Parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.Type);

        var visited = (IndexerDeclarationSyntax)base.VisitIndexerDeclaration(node)!;

        var updated = visited.WithParameterList(ApplyBracketedParameterRewrites(
            visited.ParameterList, parameterRewrites));
        if (returnPlan is TypeSyntaxRewrite plan)
            updated = updated.WithType(BuildArrayType(plan.Pointer));

        return updated;
    }

    // ---- 指针字段 / 属性 / 事件：存储位不可证 → UF020 ----

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        if (IsPointerTypeSyntax(node.Declaration.Type))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer field '{node.Declaration}' stores a managed address in a field; its target " +
                "cannot be statically proven across the field's lifetime. Rewrite it as an array/Span " +
                "field or normalize manually.",
                node.Declaration.Type);
        }

        return base.VisitFieldDeclaration(node);
    }

    public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
    {
        if (IsPointerTypeSyntax(node.Declaration.Type))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer event field '{node.Declaration}' stores a pointer; its target cannot be " +
                "proven. Rewrite as an array/Span form or normalize manually.",
                node.Declaration.Type);
        }

        return base.VisitEventFieldDeclaration(node);
    }

    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        if (IsPointerTypeSyntax(node.Type))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer property '{node.Identifier.ValueText}' exposes/stores a managed address; its " +
                "target cannot be statically proven. Rewrite as an array/Span property or normalize manually.",
                node.Type);
        }

        return base.VisitPropertyDeclaration(node);
    }

    public override SyntaxNode? VisitEventDeclaration(EventDeclarationSyntax node)
    {
        if (IsPointerTypeSyntax(node.Type))
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF020,
                $"pointer event '{node.Identifier.ValueText}' stores a pointer; its target cannot be " +
                "proven. Rewrite as an array/Span form or normalize manually.",
                node.Type);
        }

        return base.VisitEventDeclaration(node);
    }

    // ---- 指针局部变量（仅标识符别名：改写参数/局部、R2 字段）----

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        if (!IsPointerTypeSyntax(node.Declaration.Type))
            return base.VisitLocalDeclarationStatement(node);

        // 配对方法体内的 holder shadow copy-in（T* s = h.Value）与指针形参
        // 别名（T* q = p[/p±n]）：降级为 int 索引局部，flow 指向伴生/形参数组。
        if (_pairedStack.Count > 0
            && TryRewritePairedLocal(node, out var rewritten))
        {
            return rewritten;
        }

        var currentPointer = (PointerTypeSyntax)node.Declaration.Type;
        var plans = new List<LocalRewritePlan?>();
        var allRewritable = true;

        foreach (var variable in node.Declaration.Variables)
        {
            var plan = PlanLocalRewrite(variable, currentPointer);
            plans.Add(plan);
            if (plan == null) allRewritable = false;
        }

        if (!allRewritable)
        {
            // 含不可改写变量：整声明保持原样交 R3（其既有模型：fixed 别名
            // 成功，否则 UF020）。声明本身不替换。
            return base.VisitLocalDeclarationStatement(node);
        }

        var newVariables = new List<VariableDeclaratorSyntax>();
        foreach (var (variable, plan) in node.Declaration.Variables.Zip(plans))
        {
            var localSymbol = GetLocalSymbol(variable);
            var resolvedPlan = plan!;
            var rebuilt = variable.WithInitializer(EqualsValueClause(resolvedPlan.SourceExpression));
            newVariables.Add(rebuilt);

            if (localSymbol != null)
            {
                RewrittenSymbols.Add(localSymbol);
                _statistics.PointerLocalsRewritten++;
                if (_flowModel.TryGet(resolvedPlan.SourceSymbol, out var inherited))
                {
                    _flowModel.Register(localSymbol, inherited);
                }
                else if (_rewrittenFields.Contains(resolvedPlan.SourceSymbol))
                {
                    _flowModel.Register(localSymbol, new PointerFlow(
                        PointerSourceKind.ArrayField, resolvedPlan.SourceExpression.ToString()));
                }
            }
        }

        // 新声明节点（脱离旧树）：其子表达式无需 R5 再访问，R6 会整树处理。
        return node.WithDeclaration(node.Declaration
            .WithType(BuildArrayType(currentPointer))
            .WithVariables(SeparatedList(newVariables)));
    }

    // ---- 改写计划 ----

    private sealed record LocalRewritePlan(ExpressionSyntax SourceExpression, ISymbol SourceSymbol);

    /// <summary>
    /// 配对方法体内的指针局部降级为「int 索引局部」：
    /// <list type="bullet">
    /// <item><c>T* s = h.Value</c>（holder shadow copy-in）→ <c>int s = h.Value</c>，
    /// flow 数组=holder 伴生数组形参，偏移=s 自身（末尾 h.Value=s 回写不逃逸）；</item>
    /// <item><c>T* q = p</c>/<c>T* q = p + n</c>（指针形参别名）→
    /// <c>int q = pIndex</c>/<c>int q = pIndex + n</c>，flow 数组=形参数组。</item>
    /// </list>
    /// 其余形态（多变量声明、其它初值）返回 false 交既有路径。
    /// </summary>
    private bool TryRewritePairedLocal(
        LocalDeclarationStatementSyntax node,
        out StatementSyntax? rewritten)
    {
        rewritten = null;
        var plan = _pairedStack[^1];

        if (node.Declaration.Variables.Count != 1) return false;
        var declarator = node.Declaration.Variables[0];
        if (declarator.Initializer?.Value is not { } rawInitializer) return false;

        var localSymbol = GetLocalSymbol(declarator);
        if (localSymbol?.Type is not IPointerTypeSymbol) return false;

        var initializer = UnwrapParenthesized(rawInitializer);
        string? arrayName = null;
        ExpressionSyntax? newInitializer = null;

        // holder.Value copy-in。
        if (initializer is MemberAccessExpressionSyntax memberAccess
            && memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression)
            && memberAccess.Name.Identifier.ValueText == "Value"
            && GetExpressionSymbol(memberAccess.Expression) is { } holderSymbol)
        {
            var holderPlan = plan.Parameters.FirstOrDefault(p =>
                p.Kind == PairedParamKind.HolderPointer
                && SymbolEqualityComparer.Default.Equals(p.Symbol, holderSymbol));
            if (holderPlan != null)
            {
                arrayName = holderPlan.ArrayName;
                newInitializer = initializer;
            }
        }

        // 指针形参别名（标识符或 p±n 一元/二元算术）。
        if (arrayName == null
            && TryBuildPairedParamAlias(initializer, plan, out var aliasArray, out var aliasIndex))
        {
            arrayName = aliasArray;
            newInitializer = aliasIndex;
        }

        if (arrayName == null || newInitializer == null || localSymbol == null)
            return false;

        var newDeclarator = declarator.WithInitializer(EqualsValueClause(newInitializer));
        rewritten = node.WithDeclaration(node.Declaration
            .WithType(PredefinedType(Token(SyntaxKind.IntKeyword)))
            .WithVariables(SingletonSeparatedList(newDeclarator)));

        RewrittenSymbols.Add(localSymbol);
        _statistics.PointerLocalsRewritten++;
        _flowModel.Register(localSymbol, new PointerFlow(
            PointerSourceKind.ParamArray, arrayName, localSymbol.Name)
        {
            FoldsToOffset = true,
        });
        return true;
    }

    /// <summary>
    /// 初值根植于配对游标（指针形参或其 FOLDS 局部/shadow）时，把初值翻译为
    /// int 索引表达式：cursor→cursorIndex；cursor±n→cursorIndex±n（n+cursor
    /// 同样支持）。
    /// </summary>
    private bool TryBuildPairedParamAlias(
        ExpressionSyntax initializer,
        PairedMethodPlan plan,
        out string? arrayName,
        out ExpressionSyntax? indexExpression)
    {
        arrayName = null;
        indexExpression = null;

        ExpressionSyntax pointerSide;
        ExpressionSyntax? offsetSide = null;
        var subtract = false;

        if (initializer is IdentifierNameSyntax)
        {
            pointerSide = initializer;
        }
        else if (initializer is BinaryExpressionSyntax binary
                 && (binary.IsKind(SyntaxKind.AddExpression)
                     || binary.IsKind(SyntaxKind.SubtractExpression)))
        {
            var leftType = _model.GetTypeInfo(binary.Left).Type;
            var rightType = _model.GetTypeInfo(binary.Right).Type;
            if (leftType is IPointerTypeSymbol)
            {
                pointerSide = binary.Left;
                offsetSide = binary.Right;
                subtract = binary.IsKind(SyntaxKind.SubtractExpression);
            }
            else if (rightType is IPointerTypeSymbol
                     && binary.IsKind(SyntaxKind.AddExpression))
            {
                pointerSide = binary.Right;
                offsetSide = binary.Left;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var sourceSymbol = GetExpressionSymbol(pointerSide);
        if (sourceSymbol == null) return false;

        // 根为配对指针形参。
        var paramPlan = plan.Parameters.FirstOrDefault(p =>
            p.Kind == PairedParamKind.PlainPointer
            && SymbolEqualityComparer.Default.Equals(p.Symbol, sourceSymbol));
        if (paramPlan != null)
        {
            arrayName = paramPlan.ArrayName;
            var baseIndex = IdentifierName(paramPlan.IndexName);
            indexExpression = offsetSide == null
                ? baseIndex
                : AddOffset(baseIndex, offsetSide, subtract);
            return true;
        }

        // 根为同一配对方法体内已降级的 FOLDS 局部（链式游标别名/shadow）。
        if (_flowModel.TryGet(sourceSymbol, out var inherited)
            && inherited.FoldsToOffset)
        {
            arrayName = inherited.ArrayExpressionText;
            var baseIndex = IdentifierName(inherited.OffsetText);
            indexExpression = offsetSide == null
                ? baseIndex
                : AddOffset(baseIndex, offsetSide, subtract);
            return true;
        }

        return false;
    }

    private static ExpressionSyntax AddOffset(
        ExpressionSyntax baseIndex, ExpressionSyntax offset, bool subtract)
        => subtract
            ? BinaryExpression(SyntaxKind.SubtractExpression,
                ParenthesizedExpression(baseIndex), ParenthesizedExpression(offset))
            : BinaryExpression(SyntaxKind.AddExpression,
                ParenthesizedExpression(baseIndex), ParenthesizedExpression(offset));

    private List<TypeSyntaxRewrite?> PlanParameterRewrites(
        SeparatedSyntaxList<ParameterSyntax> parameters,
        BaseParameterListSyntax parameterList)
    {
        var plans = new List<TypeSyntaxRewrite?>();
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            var symbol = GetParameterSymbol(parameter);

            if (parameter.Type is PointerTypeSyntax pointer
                && symbol?.Type is IPointerTypeSymbol pointerType)
            {
                if (TryClassifyElement(pointerType.PointedAtType, out _))
                {
                    plans.Add(new TypeSyntaxRewrite(pointer));

                    if (symbol != null)
                    {
                        RewrittenSymbols.Add(symbol);
                        _statistics.PointerParametersRewritten++;
                        _flowModel.Register(symbol, new PointerFlow(
                            PointerSourceKind.ParamArray, parameter.Identifier.ValueText));

                        if (i + 1 < parameters.Count && IsLengthLikeParameter(parameters[i + 1]))
                            _statistics.PointerLengthPatterns++;
                    }
                }
                else
                {
                    RejectPointerType(parameter.Type, pointerType.PointedAtType, parameter);
                    plans.Add(null);
                }

                continue;
            }

            if (parameter.Type is FunctionPointerTypeSyntax functionPointer
                || symbol?.Type is IFunctionPointerTypeSymbol)
            {
                AddError(
                    UnsafeFfmDiagnosticCode.UF021,
                    $"function pointer parameter '{parameter.Identifier.ValueText}' (delegate*) is not " +
                    "supported by the pointer normalization whitelist; normalize manually.",
                    parameter.Type);
            }

            plans.Add(null);
        }

        return plans;
    }

    private sealed record TypeSyntaxRewrite(PointerTypeSyntax Pointer);

    private TypeSyntaxRewrite? PlanTypeRewrite(TypeSyntax type)
    {
        if (type is PointerTypeSyntax pointer)
        {
            var element = GetPointedElement(type);
            if (element != null && TryClassifyElement(element, out _))
            {
                _statistics.PointerReturnsRewritten++;
                return new TypeSyntaxRewrite(pointer);
            }

            RejectPointerType(type, element, type);
            return null;
        }

        if (type is FunctionPointerTypeSyntax)
        {
            AddError(
                UnsafeFfmDiagnosticCode.UF021,
                $"function pointer type '{type}' (delegate*) is not supported by the pointer " +
                "normalization whitelist; normalize manually.",
                type);
        }

        return null;
    }

    private LocalRewritePlan? PlanLocalRewrite(
        VariableDeclaratorSyntax variable, PointerTypeSyntax declarationPointer)
    {
        var rawInitializer = variable.Initializer?.Value;
        if (rawInitializer == null) return null;

        var initializer = UnwrapParenthesized(rawInitializer);

        // 仅接受标识符/成员访问形态，且源符号可证。
        if (initializer is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
            return null;

        var sourceSymbol = GetExpressionSymbol(initializer);
        if (sourceSymbol == null) return null;

        if (_flowModel.Contains(sourceSymbol))
            return new LocalRewritePlan(initializer, sourceSymbol);

        if (sourceSymbol is IFieldSymbol field && _rewrittenFields.Contains(field))
            return new LocalRewritePlan(initializer, sourceSymbol);

        return null;
    }

    // ---- 应用签名改写 ----

    private MethodDeclarationSyntax ApplyMethodSignature(
        MethodDeclarationSyntax visited,
        List<TypeSyntaxRewrite?> parameterRewrites,
        TypeSyntaxRewrite? returnPlan)
    {
        var updated = visited.WithParameterList(ApplyParameterRewrites(
            visited.ParameterList, parameterRewrites));
        if (returnPlan != null)
            updated = updated.WithReturnType(BuildArrayType(returnPlan.Pointer));

        return updated;
    }

    private static ParameterListSyntax ApplyParameterRewrites(
        ParameterListSyntax parameterList,
        List<TypeSyntaxRewrite?> plans)
    {
        // 无任何改写计划时原节点原样返回：SeparatedList 重建会生成全新逗号
        // token、丢失原分隔符 trivia（如 ", " 的空格），使新树与根
        // IsEquivalentTo=false，二次 RewriteFile 误报 Changed（破坏幂等）。
        if (plans.All(p => p is null))
            return parameterList;

        var parameters = new List<ParameterSyntax>();
        for (var i = 0; i < parameterList.Parameters.Count; i++)
        {
            var parameter = parameterList.Parameters[i];
            if (plans[i] is { } plan)
                parameter = parameter.WithType(BuildArrayType(plan.Pointer));
            parameters.Add(parameter);
        }

        return parameterList.WithParameters(SeparatedList(parameters));
    }

    private static BracketedParameterListSyntax ApplyBracketedParameterRewrites(
        BracketedParameterListSyntax parameterList,
        List<TypeSyntaxRewrite?> plans)
    {
        // 同 ApplyParameterRewrites：无改写即原样返回，保留分隔符 trivia。
        if (plans.All(p => p is null))
            return parameterList;

        var parameters = new List<ParameterSyntax>();
        for (var i = 0; i < parameterList.Parameters.Count; i++)
        {
            var parameter = parameterList.Parameters[i];
            if (plans[i] is { } plan)
                parameter = parameter.WithType(BuildArrayType(plan.Pointer));
            parameters.Add(parameter);
        }

        return parameterList.WithParameters(SeparatedList(parameters));
    }

    /// <summary>
    /// 由当前指针类型语法构造数组类型：复用其元素语法（保留元素写法/trivia），
    /// 维度为省略一维（<c>T[*]</c> → <c>T[]</c>）。
    /// </summary>
    private static ArrayTypeSyntax BuildArrayType(PointerTypeSyntax pointer)
    {
        var element = pointer.ElementType;
        return ArrayType(element)
            .WithRankSpecifiers(SingletonList(ArrayRankSpecifier(
                SingletonSeparatedList<ExpressionSyntax>(OmittedArraySizeExpression()))));
    }

    // ---- 元素白名单 ----

    /// <summary>
    /// 元素是否在指针签名白名单（设计 D6；任务指定 12 个基元 SpecialType）。
    /// 元素步长语义（int* +1 = 4 字节）在数组形态下天然保留：C# 数组以
    /// 元素为索引单位，M3b 字节偏移算术再按 PointedAtType 换算。
    /// </summary>
    private static bool TryClassifyElement(ITypeSymbol? element, out string reason)
    {
        reason = string.Empty;
        if (element == null)
        {
            reason = "the pointed-at type is unknown.";
            return false;
        }

        switch (element)
        {
            case ITypeParameterSymbol:
                reason = $"generic pointer '{element}*' (the pointed-at type is a generic type " +
                         "parameter); generic pointers are not supported.";
                return false;
            case IPointerTypeSymbol:
                reason = $"multi-level pointer '{element}*' is not supported.";
                return false;
            case IFunctionPointerTypeSymbol:
                reason = "function pointer (delegate*) is not supported as a pointed-at type.";
                return false;
        }

        if (element.SpecialType == SpecialType.System_Void)
        {
            reason = "void* is not supported by the pointer normalization whitelist.";
            return false;
        }

        if (element.IsReferenceType || element is IArrayTypeSymbol)
        {
            reason = $"pointer to reference/array type '{element}' is not supported.";
            return false;
        }

        if (IsWhitelistSpecial(element.SpecialType)) return true;

        if (element.TypeKind == TypeKind.Enum)
        {
            reason = $"enum pointer '{element}*' is not in the M3a primitive whitelist.";
            return false;
        }

        if (element is INamedTypeSymbol { IsValueType: true })
        {
            reason = $"non-primitive struct pointer '{element}*' is not supported (only primitive " +
                     "element pointers are normalized).";
            return false;
        }

        reason = $"pointer to '{element}' is not a supported element type.";
        return false;
    }

    private static bool IsWhitelistSpecial(SpecialType specialType) =>
        specialType is SpecialType.System_Boolean
            or SpecialType.System_Char
            or SpecialType.System_SByte
            or SpecialType.System_Byte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16
            or SpecialType.System_Int32
            or SpecialType.System_UInt32
            or SpecialType.System_Int64
            or SpecialType.System_UInt64
            or SpecialType.System_Single
            or SpecialType.System_Double;

    /// <summary>
    /// 相邻长度参数判定：整数类型（int/long/short/IntPtr 等）或名称暗示长度
    /// （len/length/count/size/num）。仅作模式旁证计数。
    /// </summary>
    private bool IsLengthLikeParameter(ParameterSyntax parameter)
    {
        var symbol = GetParameterSymbol(parameter);
        if (symbol != null)
        {
            if (symbol.Type.SpecialType is SpecialType.System_Int16
                or SpecialType.System_UInt16
                or SpecialType.System_Int32
                or SpecialType.System_UInt32
                or SpecialType.System_Int64
                or SpecialType.System_UInt64
                or SpecialType.System_IntPtr
                or SpecialType.System_UIntPtr)
            {
                return true;
            }
        }

        var name = parameter.Identifier.ValueText.ToLowerInvariant();
        return name is "len"
            || name.EndsWith("len")
            || name.EndsWith("length")
            || name.EndsWith("count")
            || name.EndsWith("size")
            || name.EndsWith("num");
    }

    /// <summary>白名单边界拒绝：按元素种类给出具体原因，UF021。</summary>
    private void RejectPointerType(
        TypeSyntax currentType, ITypeSymbol? element, SyntaxNode reportNode)
    {
        TryClassifyElement(element, out var reason);
        if (string.IsNullOrEmpty(reason))
            reason = $"pointer type '{currentType}' is not supported by the normalization whitelist.";

        AddError(
            UnsafeFfmDiagnosticCode.UF021,
            reason + " The original file was preserved; rewrite the signature manually.",
            reportNode);
    }

    // ---- 语义/语法辅助 ----

    private static bool IsPointerTypeSyntax(TypeSyntax type)
        => type is PointerTypeSyntax or FunctionPointerTypeSyntax;

    private ITypeSymbol? GetPointedElement(TypeSyntax type)
    {
        var originalNode = _resolver.Resolve(type);
        if (originalNode is PointerTypeSyntax originalPointer
            && _model.GetTypeInfo(originalPointer.ElementType).Type is { } element)
        {
            return element;
        }

        return null;
    }

    private IParameterSymbol? GetParameterSymbol(ParameterSyntax parameter)
    {
        var original = _resolver.Resolve(parameter);
        return original == null ? null : _model.GetDeclaredSymbol(original) as IParameterSymbol;
    }

    private ILocalSymbol? GetLocalSymbol(VariableDeclaratorSyntax variable)
    {
        var original = _resolver.Resolve(variable);
        return original == null ? null : _model.GetDeclaredSymbol(original) as ILocalSymbol;
    }

    private ISymbol? GetExpressionSymbol(ExpressionSyntax expression)
    {
        var original = _resolver.Resolve(expression);
        return original == null ? null : _model.GetSymbolInfo(original).Symbol;
    }

    private static ExpressionSyntax UnwrapParenthesized(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression;
    }

    private void AddError(
        UnsafeFfmDiagnosticCode code, string message, SyntaxNode node)
    {
        var line = 1;
        var character = 1;
        var positionNode = _resolver.Resolve(node) ?? node;
        try
        {
            var span = positionNode.GetLocation().GetLineSpan();
            line = span.StartLinePosition.Line + 1;
            character = span.StartLinePosition.Character + 1;
        }
        catch
        {
            // 脱离语法树的节点无法计算行号时退回 (1,1)。
        }

        Diagnostics.Add(new UnsafeFfmDiagnostic(
            UnsafeFfmSeverity.Error, code, message, null, line, character));
        _statistics.Rejections++;
        switch (code)
        {
            case UnsafeFfmDiagnosticCode.UF020: _statistics.Uf020Rejections++; break;
            case UnsafeFfmDiagnosticCode.UF021: _statistics.Uf021Rejections++; break;
        }
    }
}
