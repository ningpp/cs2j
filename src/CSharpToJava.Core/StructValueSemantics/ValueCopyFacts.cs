using CSharpToJava.Core.Transformers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// 站点改写所需的语义事实（设计 §4.3「不插的条件」列）：
/// <list type="bullet">
/// <item>新鲜值谓词与属性取值去重：口径移植 <see cref="StructCloneHelper"/>（Transformers 期同规则）。</item>
/// <item>值参只读跨过程分析：移植 IsValueParamEffectivelyReadOnly（ref 转发链/环/无体保守）。</item>
/// <item>实参→形参解析（含 NameColon、候选符号兜底；params 越界/未绑定保守返回 null）。</item>
/// <item>clone 包裹语法工厂（复杂表达式加括号）。</item>
/// </list>
/// 本类只读分析，不做改写决策之外的状态变更；只读结论按形参符号缓存。
/// </summary>
internal sealed class ValueCopyFacts
{
    private readonly Compilation _compilation;
    private readonly IReadOnlyDictionary<INamedTypeSymbol, ClonePlan> _plansBySymbol;
    private readonly Dictionary<IParameterSymbol, bool> _readOnlyCache =
        new(SymbolEqualityComparer.Default);

    public ValueCopyFacts(
        Compilation compilation,
        IReadOnlyDictionary<INamedTypeSymbol, ClonePlan> plansBySymbol)
    {
        _compilation = compilation;
        _plansBySymbol = plansBySymbol;
    }

    /// <summary>
    /// 表达式是否产出无需 clone 的新鲜值（new/方法调用/运算/默认值/字面量）。
    /// 注意：刻意不特判数组元素——C# 数组元素读出是副本，消费点必须插 clone（W4 回归）。
    /// </summary>
    public static bool IsFreshValue(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        return expression is ObjectCreationExpressionSyntax
            or ImplicitObjectCreationExpressionSyntax
            or InvocationExpressionSyntax
            or BinaryExpressionSyntax
            or PrefixUnaryExpressionSyntax
            or DefaultExpressionSyntax
            or LiteralExpressionSyntax;
    }

    /// <summary>
    /// 表达式是否为属性取值（getter 侧在转换器期已保证返回 clone，VS-2b 调用点去重）。
    /// 元素访问（索引器）不在此列——元素访问走 VS-4b 必插口径。
    /// </summary>
    public static bool IsPropertyAccess(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        if (expression is not MemberAccessExpressionSyntax and not IdentifierNameSyntax)
            return false;

        return model.GetSymbolInfo(expression).Symbol is IPropertySymbol;
    }

    /// <summary>
    /// 实参表达式是否为 struct 数组元素读取（VS-4b：消费点单点包裹，必插）。
    /// </summary>
    public static bool IsArrayElementLoad(ExpressionSyntax expression, SemanticModel model)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;

        if (expression is not ElementAccessExpressionSyntax elementAccess) return false;
        var receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
        return receiverType is IArrayTypeSymbol;
    }

    /// <summary>
    /// 解析实参对应的形参（位置/NameColon；dynamic/未绑定/params 展开越界 → null，调用点保守处理）。
    /// </summary>
    public static IParameterSymbol? ResolveArgumentParameter(ArgumentSyntax argument, SemanticModel model)
    {
        if (argument.Parent is not ArgumentListSyntax argumentList) return null;
        var invocation = argumentList.Parent;
        if (invocation == null) return null;

        var symbolInfo = model.GetSymbolInfo(invocation);
        var method = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (method == null) return null;

        var parameters = method.Parameters;
        if (parameters.Length == 0) return null;

        if (argument.NameColon?.Name.Identifier.ValueText is { } name)
            return parameters.FirstOrDefault(p => p.Name == name);

        var index = argumentList.Arguments
            .Where(a => a.NameColon == null)
            .TakeWhile(a => a != argument)
            .Count();

        return index < parameters.Length ? parameters[index] : null;
    }

    /// <summary>
    /// 值参在被调方体内「效果只读」（不重绑参数、不写字段、不 ref/out 转发给可变方）时，
    /// VS-2b 调用点可跳过 clone。无语法体（外部程序集/partial 缺部件）保守返回 false。
    /// </summary>
    public bool IsValueParameterEffectivelyReadOnly(IParameterSymbol parameter)
    {
        if (parameter.RefKind != RefKind.None) return false;
        if (!StructCloneHelper.IsUserDefinedStruct(parameter.Type)) return false;
        if (parameter.ContainingSymbol is not IMethodSymbol) return false;

        if (_readOnlyCache.TryGetValue(parameter, out var cached)) return cached;
        var result = IsValueParamReadOnlyCore(
            parameter, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        _readOnlyCache[parameter] = result;
        return result;
    }

    // ── 跨过程只读分析（移植自 StructCloneHelper，环检测 + 无体保守） ──

    private bool IsValueParamReadOnlyCore(IParameterSymbol parameter, HashSet<ISymbol> visited)
    {
        var method = parameter.ContainingSymbol;
        if (method == null) return false;
        if (!visited.Add(parameter)) return false; // 递归环 → 保守

        var foundAnyBody = false;
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            SyntaxNode? body = GetMethodBody(syntax);
            if (body == null) continue;
            foundAnyBody = true;

            var model = _compilation.GetSemanticModel(syntaxRef.SyntaxTree);
            var paramName = parameter.Name;
            foreach (var node in body.DescendantNodes())
            {
                // p = expr
                if (node is AssignmentExpressionSyntax assignment
                    && assignment.Left is IdentifierNameSyntax lhsIdent
                    && lhsIdent.Identifier.Text == paramName)
                {
                    return false;
                }

                // p.field = expr / p[i].field = expr
                if (node is AssignmentExpressionSyntax fieldAssign
                    && IsChainedMemberAccessFrom(fieldAssign.Left, paramName))
                {
                    return false;
                }

                // Method(ref p) → 被转发形参也必须只读；Method(out p)/Method(ref p.f) → 不可证。
                if (node is ArgumentSyntax arg
                    && (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
                        || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)))
                {
                    if (arg.Expression is IdentifierNameSyntax refIdent
                        && refIdent.Identifier.Text == paramName)
                    {
                        if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) return false;
                        if (!IsRefForwardingSafe(arg, model, visited)) return false;
                        continue;
                    }
                    if (IsChainedMemberAccessFrom(arg.Expression, paramName)) return false;
                }

                if (IsUnaryMutationOf(node, paramName)) return false;

                // p.Mutate(...) / p.PropOrMethodThatMutates(...)：接收者根为该形参，
                // 且被调方法属于所属残余 struct 的变异方法闭包（analyzer 已做定点闭包）→ 变异。
                if (node is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax memberAccess
                    } invocation
                    && RootReceiverIs(memberAccess.Expression, paramName)
                    && ResolvesToPlanMutatingMethod(invocation, model))
                {
                    return false;
                }
            }
        }

        return foundAnyBody;
    }

    private bool ResolvesToPlanMutatingMethod(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol callee)
            return false;
        if (!_plansBySymbol.TryGetValue(
                callee.ContainingType.OriginalDefinition, out var plan))
        {
            return false;
        }
        if (!plan.IsActive) return false;

        var calleeDefinition = callee.OriginalDefinition;
        return plan.MutatingMethods.Any(m =>
            SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, calleeDefinition));
    }

    /// <summary>剥掉成员/元素访问链，接收者根是否恰为名为 <paramref name="identifierName"/> 的标识符。</summary>
    private static bool RootReceiverIs(ExpressionSyntax expression, string identifierName)
    {
        var current = expression;
        while (current is MemberAccessExpressionSyntax ma)
            current = ma.Expression;
        while (current is ElementAccessExpressionSyntax ea)
            current = ea.Expression;
        while (current is ParenthesizedExpressionSyntax paren)
            current = paren.Expression;
        return current is IdentifierNameSyntax id && id.Identifier.Text == identifierName;
    }

    private bool IsRefForwardingSafe(ArgumentSyntax arg, SemanticModel model, HashSet<ISymbol> visited)
    {
        var invocation = arg.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation == null) return false;
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol calledMethod)
            return false;

        var argumentList = arg.Parent as ArgumentListSyntax;
        if (argumentList == null) return false;
        var argIndex = argumentList.Arguments.IndexOf(arg);
        if (argIndex < 0 || argIndex >= calledMethod.Parameters.Length) return false;

        return IsValueParamReadOnlyCore(calledMethod.Parameters[argIndex], visited);
    }

    private static SyntaxNode? GetMethodBody(SyntaxNode syntax) => syntax switch
    {
        MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
        LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
        ConstructorDeclarationSyntax c => (SyntaxNode?)c.Body ?? c.ExpressionBody,
        _ => null,
    };

    private static bool IsChainedMemberAccessFrom(ExpressionSyntax expression, string identifierName)
    {
        if (expression is not MemberAccessExpressionSyntax) return false;
        var current = expression;
        while (current is MemberAccessExpressionSyntax ma)
            current = ma.Expression;
        while (current is ElementAccessExpressionSyntax ea)
            current = ea.Expression;
        return current is IdentifierNameSyntax id && id.Identifier.Text == identifierName;
    }

    private static bool IsUnaryMutationOf(SyntaxNode node, string paramName)
    {
        if (node is PrefixUnaryExpressionSyntax prefix && IsIdentOrChainedFrom(prefix.Operand, paramName))
            return true;
        if (node is PostfixUnaryExpressionSyntax postfix && IsIdentOrChainedFrom(postfix.Operand, paramName))
            return true;
        return false;
    }

    private static bool IsIdentOrChainedFrom(ExpressionSyntax expr, string identifierName)
    {
        if (expr is IdentifierNameSyntax id && id.Identifier.Text == identifierName)
            return true;
        return IsChainedMemberAccessFrom(expr, identifierName);
    }

    /// <summary>
    /// 生成 <c>e.Clone()</c>；简单表达式直接挂接收者，条件/二元等复杂形态加括号。
    /// </summary>
    public static InvocationExpressionSyntax WrapClone(ExpressionSyntax expression)
    {
        ExpressionSyntax receiver = CanCallCloneDirectly(expression)
            ? expression.WithoutTrivia()
            : SyntaxFactory.ParenthesizedExpression(expression.WithoutTrivia());
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                receiver,
                SyntaxFactory.IdentifierName(CloneShimGenerator.CloneMethodName)));
    }

    private static bool CanCallCloneDirectly(ExpressionSyntax expression) => expression is
        IdentifierNameSyntax
        or ThisExpressionSyntax
        or BaseExpressionSyntax
        or MemberAccessExpressionSyntax
        or ElementAccessExpressionSyntax
        or InvocationExpressionSyntax
        or ObjectCreationExpressionSyntax
        or ImplicitObjectCreationExpressionSyntax
        or PostfixUnaryExpressionSyntax;
}
