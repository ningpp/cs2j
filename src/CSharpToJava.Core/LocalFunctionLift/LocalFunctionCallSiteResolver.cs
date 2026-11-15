using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.LocalFunctionLift;

/// <summary>
/// 局部函数调用/引用节点的共享解析器（分析器与重写器共用）。
///
/// 解析顺序严格按 C# 语言规则：
/// <list type="number">
/// <item>已绑定符号（含构造泛型方法的 OriginalDefinition 回退）；</item>
/// <item>绑定失败时的 <see cref="SymbolInfo.CandidateSymbols"/> 中唯一的局部函数候选；</item>
/// <item>词法作用域：从引用节点向上找最近的语句容器，容器内同名局部函数即候选；
///       同一容器内的重载再按实参数目 / ref-kind / 命名实参消歧。</item>
/// </list>
/// 只有能唯一确定时才返回 plan；否则返回 <see langword="null"/>，由调用方保留原样
/// 并给出诊断——绝不按名字猜测（同名后者覆盖前者会改错重载）。
/// </summary>
internal static class LocalFunctionCallSiteResolver
{
    /// <summary>
    /// 把 <paramref name="reference"/> 解析到唯一的非跳过 <see cref="LiftPlan"/>。
    /// </summary>
    /// <param name="reference">
    /// 调用表达式（<see cref="InvocationExpressionSyntax"/>），或方法组引用所用的
    /// 简单名节点。实参消歧只在能取到调用实参时进行。
    /// </param>
    /// <param name="name">引用处书写的方法名。</param>
    /// <param name="plans">候选局部函数计划（通常是非跳过的全部 plan）。</param>
    /// <param name="model">该语法树对应的语义模型。</param>
    /// <param name="symbolToPlan">
    /// 调用方已建好的符号索引（可包含已跳过 plan）；为 null 时按 <paramref name="plans"/> 现建。
    /// </param>
    /// <param name="includeSkipped">
    /// 词法作用域 / 候选符号阶段是否也考虑已跳过的 plan（Gate #12 需要探测对跳过函数的引用）。
    /// </param>
    internal static LiftPlan? Resolve(
        SyntaxNode reference,
        string name,
        IReadOnlyList<LiftPlan> plans,
        SemanticModel model,
        IReadOnlyDictionary<ISymbol, LiftPlan>? symbolToPlan = null,
        bool includeSkipped = false)
    {
        var bySymbol = symbolToPlan ?? BuildSymbolMap(plans, includeSkipped);
        var symbolInfo = model.GetSymbolInfo(reference);

        // a. 已绑定符号：命中符号表即局部函数；绑定到其它成员则不是本 pass 的目标。
        if (symbolInfo.Symbol != null)
        {
            if (TryGetPlan(bySymbol, symbolInfo.Symbol, out var bound))
                return bound;
            return null;
        }

        // b. 候选符号：恰好映射到一个（且签名与实参相容的）plan 时命中；
        //    多个候选继续走词法作用域。OverloadResolutionFailure 的候选列表并不
        //    保证唯一正确（可能只列出第一个重载），必须用实参再校验一次。
        var invocation = FindInvocation(reference);
        LiftPlan? candidate = null;
        var candidateCount = 0;
        foreach (var candidateSymbol in symbolInfo.CandidateSymbols)
        {
            if (candidateSymbol is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } method)
                continue;
            if (!TryGetPlan(bySymbol, method, out var plan))
                continue;
            if (!includeSkipped && plan.Skipped)
                continue;
            if (!string.Equals(plan.Node.Identifier.Text, name, StringComparison.Ordinal))
                continue;
            if (invocation != null
                && !SignatureAccepts(plan.Node, invocation.ArgumentList.Arguments))
                continue;

            candidate = plan;
            candidateCount++;
        }

        if (candidateCount == 1)
            return candidate;

        // c. 词法作用域 + 重载消歧。
        return ResolveByLexicalScope(reference, name, plans, includeSkipped);
    }

    /// <summary>
    /// 词法作用域解析：局部函数在其声明所在的语句容器内全局可见（含声明点之前），
    /// 更内层的同名声明遮蔽外层。同一容器内多个同名 plan 是重载，按实参消歧。
    /// </summary>
    private static LiftPlan? ResolveByLexicalScope(
        SyntaxNode reference,
        string name,
        IReadOnlyList<LiftPlan> plans,
        bool includeSkipped)
    {
        var invocation = FindInvocation(reference);
        for (var ancestor = reference.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!IsStatementContainer(ancestor))
                continue;

            var inScope = new List<LiftPlan>();
            foreach (var plan in plans)
            {
                if (!includeSkipped && plan.Skipped)
                    continue;
                if (!ReferenceEquals(plan.Node.Parent, ancestor))
                    continue;
                if (!string.Equals(plan.Node.Identifier.Text, name, StringComparison.Ordinal))
                    continue;
                inScope.Add(plan);
            }

            if (inScope.Count == 0)
                continue;
            if (inScope.Count == 1)
                return inScope[0];

            // 最近的同名语句容器：C# 遮蔽规则决定外层同名声明不可见，
            // 若本层重载无法唯一确定，只能返回 null（不向外层误配）。
            return DisambiguateByArguments(inScope, invocation);
        }

        return null;
    }

    /// <summary>
    /// 同一语句容器内的重载消歧：
    /// 1) 必填参数个数 ≤ 实参个数 ≤ 全部参数个数（params 兼容），且 ref-kind / 命名实参匹配；
    /// 2) 仍多个时优先「非 params 且参数个数相等」；
    /// 3) 仍多个返回 null。
    /// </summary>
    private static LiftPlan? DisambiguateByArguments(
        IReadOnlyList<LiftPlan> candidates,
        InvocationExpressionSyntax? invocation)
    {
        if (invocation == null)
            return null;

        var arguments = invocation.ArgumentList.Arguments;
        var matching = new List<LiftPlan>();
        foreach (var plan in candidates)
        {
            if (SignatureAccepts(plan.Node, arguments))
                matching.Add(plan);
        }

        if (matching.Count == 1)
            return matching[0];
        if (matching.Count == 0)
            return null;

        LiftPlan? exact = null;
        var exactCount = 0;
        foreach (var plan in matching)
        {
            if (HasParams(plan.Node))
                continue;
            if (plan.Node.ParameterList?.Parameters.Count != arguments.Count)
                continue;
            exact = plan;
            exactCount++;
        }

        return exactCount == 1 ? exact : null;
    }

    /// <summary>局部函数签名能否接受这组实参（数目、params、ref-kind、命名实参）。</summary>
    private static bool SignatureAccepts(
        LocalFunctionStatementSyntax localFn,
        SeparatedSyntaxList<ArgumentSyntax> arguments)
    {
        var parameters = localFn.ParameterList?.Parameters ?? default;
        var hasParams = HasParams(localFn);

        var required = 0;
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            if (parameter.Modifiers.Any(static m => m.IsKind(SyntaxKind.ParamsKeyword)))
                continue;
            if (parameter.Default == null)
                required++;
        }

        if (arguments.Count < required)
            return false;
        if (!hasParams && arguments.Count > parameters.Count)
            return false;
        if (parameters.Count == 0)
            return arguments.Count == 0;

        var positionalIndex = 0;
        foreach (var argument in arguments)
        {
            if (argument.NameColon != null)
            {
                var parameterName = argument.NameColon.Name.Identifier.Text;
                var named = parameters.FirstOrDefault(parameter =>
                    string.Equals(parameter.Identifier.Text, parameterName, StringComparison.Ordinal));
                if (named == null)
                    return false;
                if (!RefKindMatches(argument, named))
                    return false;
                continue;
            }

            var index = positionalIndex < parameters.Count
                ? positionalIndex
                : parameters.Count - 1;
            positionalIndex++;

            // 多余实参全部由 params 数组吸收（params 形参本身没有 ref-kind）。
            if (hasParams && index == parameters.Count - 1)
                continue;
            if (!RefKindMatches(argument, parameters[index]))
                return false;
        }

        return true;
    }

    private static bool RefKindMatches(ArgumentSyntax argument, ParameterSyntax parameter)
    {
        var argumentKind = argument.RefKindKeyword.Kind() switch
        {
            SyntaxKind.RefKeyword => SyntaxKind.RefKeyword,
            SyntaxKind.OutKeyword => SyntaxKind.OutKeyword,
            SyntaxKind.InKeyword => SyntaxKind.InKeyword,
            _ => SyntaxKind.None,
        };

        var parameterKind = parameter.Modifiers.Any(static m => m.IsKind(SyntaxKind.OutKeyword))
            ? SyntaxKind.OutKeyword
            : parameter.Modifiers.Any(static m => m.IsKind(SyntaxKind.RefKeyword))
                ? SyntaxKind.RefKeyword
                : parameter.Modifiers.Any(static m => m.IsKind(SyntaxKind.InKeyword))
                    ? SyntaxKind.InKeyword
                    : SyntaxKind.None;

        if (argumentKind == parameterKind)
            return true;

        // `in` 形参可以按值传实参（省略 in 关键字）。
        return argumentKind == SyntaxKind.None && parameterKind == SyntaxKind.InKeyword;
    }

    private static bool HasParams(LocalFunctionStatementSyntax localFn)
        => localFn.ParameterList != null
           && localFn.ParameterList.Parameters.Any(
               static parameter => parameter.Modifiers.Any(SyntaxKind.ParamsKeyword));

    private static InvocationExpressionSyntax? FindInvocation(SyntaxNode node)
    {
        if (node is InvocationExpressionSyntax invocation)
            return invocation;
        if (node.Parent is InvocationExpressionSyntax parent && parent.Expression == node)
            return parent;
        return null;
    }

    /// <summary>局部函数声明所在的语句容器类型（词法作用域单位）。</summary>
    private static bool IsStatementContainer(SyntaxNode node)
        => node is BlockSyntax
            or SwitchSectionSyntax
            or ArrowExpressionClauseSyntax
            or EqualsValueClauseSyntax
            or CompilationUnitSyntax;

    private static bool TryGetPlan(
        IReadOnlyDictionary<ISymbol, LiftPlan> bySymbol,
        ISymbol symbol,
        out LiftPlan plan)
    {
        if (bySymbol.TryGetValue(symbol, out var direct))
        {
            plan = direct;
            return true;
        }

        // 显式类型实参的方法组引用绑定到构造泛型方法，而 plan 按声明符号索引。
        if (symbol is IMethodSymbol method
            && !SymbolEqualityComparer.Default.Equals(method, method.OriginalDefinition)
            && bySymbol.TryGetValue(method.OriginalDefinition, out var definition))
        {
            plan = definition;
            return true;
        }

        plan = null!;
        return false;
    }

    private static Dictionary<ISymbol, LiftPlan> BuildSymbolMap(
        IReadOnlyList<LiftPlan> plans,
        bool includeSkipped)
    {
        var map = new Dictionary<ISymbol, LiftPlan>(SymbolEqualityComparer.Default);
        foreach (var plan in plans)
        {
            if (plan.Symbol == null)
                continue;
            if (!includeSkipped && plan.Skipped)
                continue;
            map[plan.Symbol] = plan;
            map[plan.Symbol.OriginalDefinition] = plan;
        }

        return map;
    }
}
