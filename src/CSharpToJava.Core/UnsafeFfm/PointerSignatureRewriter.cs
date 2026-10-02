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

    public PointerSignatureRewriter(
        SemanticModel model,
        HashSet<IFieldSymbol> rewrittenFields,
        PointerFlowModel flowModel,
        UnsafeFfmStatistics statistics,
        OriginalSyntaxResolver resolver)
    {
        _model = model;
        _rewrittenFields = rewrittenFields;
        _flowModel = flowModel;
        _statistics = statistics;
        _resolver = resolver;
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
        var parameters = node.ParameterList.Parameters;
        var parameterRewrites = PlanParameterRewrites(parameters, node.ParameterList);
        var returnPlan = PlanTypeRewrite(node.ReturnType);

        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        return ApplyMethodSignature(visited, parameterRewrites, returnPlan);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
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
