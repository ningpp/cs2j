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
        {
            // OverloadResolutionFailure 的候选列表并不保证完整：Roslyn 往往只列出
            // 「第一个」重载（实测 `Add(error, "text")` 只给出 Add(object, object)），
            // 而实参里可能还有类型完好的实参足以选出真正更好的重载。同一作用域内
            // 存在多个同名重载时，先用类型证据再消歧一次；拿不到唯一结论时才回到
            // Roslyn 的候选（保持既有行为，不因为分析编译的错误类型新增跳过）。
            var scoped = ResolveByLexicalScope(reference, name, plans, includeSkipped, model);
            if (scoped != null && !ReferenceEquals(scoped, candidate))
                return scoped;
            return candidate;
        }

        // c. 词法作用域 + 重载消歧。
        return ResolveByLexicalScope(reference, name, plans, includeSkipped, model);
    }

    /// <summary>
    /// 词法作用域解析：局部函数在其声明所在的语句容器内全局可见（含声明点之前），
    /// 更内层的同名声明遮蔽外层。同一容器内多个同名 plan 是重载，按实参消歧。
    /// </summary>
    private static LiftPlan? ResolveByLexicalScope(
        SyntaxNode reference,
        string name,
        IReadOnlyList<LiftPlan> plans,
        bool includeSkipped,
        SemanticModel model)
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
                if (!ReferenceEquals(GetStatementContainer(plan.Node), ancestor))
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
            return DisambiguateByArguments(inScope, invocation, model);
        }

        return null;
    }

    /// <summary>
    /// 同一语句容器内的重载消歧：
    /// 1) 必填参数个数 ≤ 实参个数 ≤ 全部参数个数（params 兼容），且 ref-kind / 命名实参匹配；
    /// 2) 用类型可用的实参做重载择优（实参类型与形参类型逐一比较，仅接受唯一更优者）；
    /// 3) 仍多个时优先「非 params 且参数个数相等」；
    /// 4) 仍多个返回 null。
    /// </summary>
    private static LiftPlan? DisambiguateByArguments(
        IReadOnlyList<LiftPlan> candidates,
        InvocationExpressionSyntax? invocation,
        SemanticModel model)
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

        var typed = DisambiguateByArgumentTypes(matching, arguments, model);
        if (typed != null)
            return typed;

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

    /// <summary>
    /// 用「类型可用」的实参做重载择优：对每个候选逐个比较实参类型与形参类型，
    /// 只有恰好一个候选在所有可比实参上都是恒等转换时才返回它，否则返回 null
    /// （无证据，不猜）。类型不可用的实参（ErrorType、null/default 字面量、
    /// lambda、方法组、ref/out 实参、动态或类型参数）一律不参与比较。
    ///
    /// 语义依据：可比实参上的恒等转换是 C# 重载择优的「更优转换」的最强证据。
    /// 若源工程能编译，另一个候选不可能在这些实参上同样更优；因此唯一恒等候选
    /// 就是真实编译器会选中的重载。分析编译里由 global using 泄漏造成的错误
    /// 类型只影响不可比实参，不参与判断。
    /// </summary>
    private static LiftPlan? DisambiguateByArgumentTypes(
        IReadOnlyList<LiftPlan> candidates,
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        SemanticModel model)
    {
        // 命名实参 / params / 可选参数会打乱「第 i 个实参对应第 i 个形参」的
        // 直接映射；这些形态一律交给后面的保守规则。
        if (arguments.Any(static a => a.NameColon != null))
            return null;

        var parameterTypes = new List<List<IParameterSymbol>>(candidates.Count);
        foreach (var plan in candidates)
        {
            var parameters = plan.Node.ParameterList?.Parameters;
            if (parameters == null
                || parameters.Value.Count != arguments.Count
                || HasParams(plan.Node)
                || parameters.Value.Any(static p => p.Default != null))
            {
                return null;
            }

            var symbols = new List<IParameterSymbol>(parameters.Value.Count);
            foreach (var parameter in parameters.Value)
            {
                if (model.GetDeclaredSymbol(parameter) is not IParameterSymbol symbol)
                    return null;

                // 形参类型本身不可用（ErrorType / 未构造的泛型形参）时，转换判定会
                // 失真；直接放弃类型消歧，回到既有规则。
                if (symbol.Type.TypeKind == TypeKind.Error || ContainsTypeParameter(symbol.Type))
                    return null;
                symbols.Add(symbol);
            }
            parameterTypes.Add(symbols);
        }

        var comparable = new List<int>(arguments.Count);
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!IsComparableArgument(arguments[i], model))
                continue;
            comparable.Add(i);
        }

        if (comparable.Count == 0)
            return null;

        // 先排除「无法隐式接受某个可比实参」的候选；只剩一个时它也是唯一可用的。
        var applicable = new List<int>(candidates.Count);
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var acceptsAll = true;
            foreach (var argumentIndex in comparable)
            {
                var conversion = model.ClassifyConversion(
                    arguments[argumentIndex].Expression,
                    parameterTypes[candidateIndex][argumentIndex].Type);
                if (!conversion.IsImplicit)
                {
                    acceptsAll = false;
                    break;
                }
            }

            if (acceptsAll)
                applicable.Add(candidateIndex);
        }

        if (applicable.Count == 0)
            return null;
        if (applicable.Count == 1)
            return candidates[applicable[0]];

        var unique = -1;
        foreach (var candidateIndex in applicable)
        {
            var allIdentity = true;
            foreach (var argumentIndex in comparable)
            {
                var argumentType = model.GetTypeInfo(
                    arguments[argumentIndex].Expression).Type;
                if (argumentType == null
                    || !SymbolEqualityComparer.Default.Equals(
                        argumentType,
                        parameterTypes[candidateIndex][argumentIndex].Type))
                {
                    allIdentity = false;
                    break;
                }
            }

            if (!allIdentity)
                continue;
            if (unique >= 0)
                return null;
            unique = candidateIndex;
        }

        return unique >= 0 ? candidates[unique] : null;
    }

    /// <summary>
    /// 实参类型是否可用于重载择优：必须是有确定类型、非错误类型、非动态、
    /// 非类型参数的普通表达式，且不是 ref/out/in 实参。
    /// </summary>
    private static bool IsComparableArgument(ArgumentSyntax argument, SemanticModel model)
    {
        if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
            return false;

        var type = model.GetTypeInfo(argument.Expression).Type;
        return type != null
            && type.TypeKind != TypeKind.Error
            && type.TypeKind != TypeKind.Dynamic
            && type is not ITypeParameterSymbol;
    }

    /// <summary>类型（含数组元素、指针目标、泛型实参）是否含未构造的类型形参。</summary>
    private static bool ContainsTypeParameter(ITypeSymbol type)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                return true;
            case IArrayTypeSymbol array:
                return ContainsTypeParameter(array.ElementType);
            case IPointerTypeSymbol pointer:
                return ContainsTypeParameter(pointer.PointedAtType);
            case INamedTypeSymbol named:
                return named.TypeArguments.Any(ContainsTypeParameter);
            default:
                return false;
        }
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

    /// <summary>
    /// 局部函数声明所在的词法语句容器。顶层局部函数被
    /// <see cref="GlobalStatementSyntax"/> 包住，作用域单位是编译单元（与
    /// <see cref="ResolveByLexicalScope"/> 的祖先判定保持一致）。
    /// </summary>
    internal static SyntaxNode? GetStatementContainer(LocalFunctionStatementSyntax localFn)
        => localFn.Parent is GlobalStatementSyntax { Parent: CompilationUnitSyntax unit }
            ? unit
            : localFn.Parent;

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
