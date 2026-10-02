using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// Phase 1c：跨整树枚举残余可变 struct 的值流站点（设计 §4.3 全表）。
/// VS-1a/1b/1c、VS-2a/2b、VS-3、VS-4a/b、VS-5a/b/c。
/// VS-4b（数组元素读取）按「消费点单点包裹」总原则不单列锚点——其消费形态
/// （local init/赋值/return/实参）已由上述站点捕获，rewriter 包裹时按元素读取计统计。
/// 判定优先级 VS-5 → VS-3 → VS-4 → VS-2/VS-1：先收集 VS-5 cast 锚点（pass 1），
/// 其余站点（pass 2）命中同一锚点时让位，避免双重包裹。
/// 类型判定一律走 SemanticModel，泛型实例化经 OriginalDefinition 归到唯一定义 plan。
/// </summary>
internal sealed class StructValueSemanticsSiteEnumerator
{
    private readonly StructValueSemanticsAnalysis _analysis;
    private readonly Compilation _compilation;
    private readonly Dictionary<INamedTypeSymbol, ClonePlan> _plans;

    /// <summary>VS-5c 拆箱 cast 节点：同锚点的 VS-1/2 站点让位。</summary>
    private readonly HashSet<SyntaxNode> _priorityAnchors = new();

    /// <summary>VS-5c：<c>o is S x</c> 声明模式绑定的局部——拆箱副本，按只读槽处理。</summary>
    private readonly HashSet<ISymbol> _unboxPatternLocals =
        new(SymbolEqualityComparer.Default);

    /// <summary>plan × 接口定义 → 接口是否含命中变异闭包的成员（缓存）。</summary>
    private readonly Dictionary<(ClonePlan Plan, INamedTypeSymbol Interface), bool> _mutatingInterfaceCache =
        new(new PlanInterfaceComparer());

    private readonly HashSet<string> _sv013Files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sv014Files = new(StringComparer.OrdinalIgnoreCase);

    public StructValueSemanticsSiteEnumerator(StructValueSemanticsAnalysis analysis)
    {
        _analysis = analysis;
        _compilation = analysis.Compilation;
        _plans = analysis.PlansBySymbol;
    }

    public void Enumerate()
    {
        foreach (var tree in _compilation.SyntaxTrees)
        {
            var root = tree.GetRoot();
            var model = _compilation.GetSemanticModel(tree);
            var allNodes = root.DescendantNodes().ToList();

            // pass 1：VS-5 cast（(object)s / (IFoo)s / (S)o）+ is-pattern 拆箱局部
            foreach (var node in allNodes.OfType<CastExpressionSyntax>())
                VisitCast(node, model);
            foreach (var pattern in allNodes.OfType<DeclarationPatternSyntax>())
                VisitDeclarationPattern(pattern, model);

            // pass 2：其余值流站点
            foreach (var node in allNodes)
            {
                switch (node)
                {
                    case VariableDeclaratorSyntax declarator:
                        VisitDeclarator(declarator, model);
                        break;
                    case AssignmentExpressionSyntax assignment:
                        VisitAssignment(assignment, model);
                        break;
                    case ReturnStatementSyntax { Expression: not null } ret:
                        VisitReturn(ret, model);
                        break;
                    case ArgumentSyntax argument:
                        VisitArgument(argument, model);
                        break;
                    case InvocationExpressionSyntax invocation:
                        VisitDefensiveReceiver(invocation, model);
                        break;
                }
            }
        }

        EmitSkipInfo(StructValueSemanticsDiagnosticCode.SV013, _sv013Files,
            "value-copy sites inside expression-tree lambdas were skipped.");
        EmitSkipInfo(StructValueSemanticsDiagnosticCode.SV014, _sv014Files,
            "dynamically bound value-copy sites were skipped.");
    }

    private void EmitSkipInfo(
        StructValueSemanticsDiagnosticCode code,
        HashSet<string> files,
        string message)
    {
        foreach (var file in files)
        {
            _analysis.PlanningDiagnostics.Add(new StructValueSemanticsDiagnostic(
                StructValueSemanticsSeverity.Info, code, message, file, 0, 0, null));
        }
    }

    private ClonePlan? ActivePlanForType(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named) return null;
        return _plans.TryGetValue(named.OriginalDefinition, out var plan) && plan.IsActive
            ? plan
            : null;
    }

    // VS-1a：S x = e;（local / 字段 / for 初始化器；ref/in 局部除外）；VS-6：无初始化器实例字段登记
    private void VisitDeclarator(VariableDeclaratorSyntax declarator, SemanticModel model)
    {
        // 幂等：本 pass 上一轮提升的临时变量（链式共享右值 __cs2jSc / 防御接收者副本
        // __cs2jVc）重放时不是新站点——链式临时故意持有未克隆的共享右值，各目标槽
        // 已在展开语句中逐个 Clone；再对其初始化器插 clone 会产出 a.Clone() 的重复包裹。
        var declaratorName = declarator.Identifier.Text;
        if (declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.ChainedAssignTempPrefix,
                StringComparison.Ordinal)
            || declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.DefensiveCopyTempPrefix,
                StringComparison.Ordinal))
        {
            return;
        }

        if (declarator.Initializer?.Value is not { } value)
        {
            // VS-6：无初始化器的 struct 类型实例字段——零值/默认构造语义另由既有路径负责，仅登记。
            if (model.GetDeclaredSymbol(declarator) is IFieldSymbol { IsStatic: false } uninitializedField
                && ActivePlanForType(uninitializedField.Type) != null)
            {
                _analysis.PlanningStatistics.UninitializedStructFields++;
            }
            return;
        }
        if (IsInsideExpressionTree(declarator, model)) return;

        ITypeSymbol? declaredType = model.GetDeclaredSymbol(declarator) switch
        {
            ILocalSymbol { RefKind: RefKind.None } local => local.Type,
            IFieldSymbol field => field.Type,
            _ => null,
        };
        if (declaredType == null) return;

        if (_priorityAnchors.Contains(value)) return;

        if (TryClassifyConversionSite(value, declaredType, model, declarator,
                out var kind, out var plan))
        {
            AddSite(plan!, kind, value, declarator);
            return;
        }

        var directPlan = ActivePlanForType(declaredType);
        if (directPlan != null && !ValueFlowIsFresh(value))
            AddSite(directPlan, SiteKind.LocalInit, value, declarator);
    }

    // VS-1b/1c/4a + VS-5 赋值：赋值（含链式）；只处理简单赋值，复合赋值是原位变异。
    private void VisitAssignment(AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        if (!assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return;

        // 链式 a = b = e：内层 b = e 作为外层 RHS 时不单独建站点（由外层展开统管）。
        if (assignment.Parent is AssignmentExpressionSyntax outer && outer.Right == assignment)
            return;

        if (IsInsideExpressionTree(assignment, model)) return;

        // ref-returning 方法/属性/索引器槽赋值是别名写入，属 SV040/SV042 保守族；不按值站点处理。
        if (assignment.Left is InvocationExpressionSyntax) return;
        var leftSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
        if (leftSymbol is ILocalSymbol { RefKind: not RefKind.None }) return;
        if (leftSymbol is IPropertySymbol { ReturnsByRef: true }) return;

        var lhsType = model.GetTypeInfo(assignment.Left).Type;
        if (lhsType == null) return;

        if (assignment.Right is AssignmentExpressionSyntax)
        {
            // 链式目标槽类型必须是残余 struct（值类型一致）；链式不支持装箱目标。
            var chainPlan = ActivePlanForType(lhsType);
            if (chainPlan != null)
            {
                AddSite(chainPlan, SiteKind.ChainedAssign, assignment, assignment,
                    isStatementPosition: assignment.Parent is ExpressionStatementSyntax);
            }
            return;
        }

        var value = assignment.Right;
        if (_priorityAnchors.Contains(value)) return;

        if (TryClassifyConversionSite(value, lhsType, model, assignment,
                out var kind, out var boxingPlan))
        {
            AddSite(boxingPlan!, kind, value, assignment);
            return;
        }

        var plan = ActivePlanForType(lhsType);
        if (plan == null || ValueFlowIsFresh(value)) return;

        if (assignment.Left is ElementAccessExpressionSyntax elementAccess
            && IsArrayReceiver(elementAccess, model))
        {
            AddSite(plan, SiteKind.ArrayStore, value, assignment);
            return;
        }

        AddSite(plan, SiteKind.Assign, value, assignment);
    }

    // VS-2a / VS-5 返回：return e;
    private void VisitReturn(ReturnStatementSyntax ret, SemanticModel model)
    {
        if (model.GetEnclosingSymbol(ret.SpanStart) is not IMethodSymbol enclosing) return;
        if (IsInsideExpressionTree(ret, model)) return;

        var value = ret.Expression!;
        if (_priorityAnchors.Contains(value)) return;

        if (TryClassifyConversionSite(value, enclosing.ReturnType, model, ret,
                out var kind, out var boxingPlan))
        {
            AddSite(boxingPlan!, kind, value, ret);
            return;
        }

        var plan = ActivePlanForType(enclosing.ReturnType);
        if (plan != null && !ValueFlowIsFresh(value))
            AddSite(plan, SiteKind.Return, value, ret);
    }

    // VS-2b / VS-5 值参
    private void VisitArgument(ArgumentSyntax argument, SemanticModel model)
    {
        if (argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)
            || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
            || argument.RefKindKeyword.IsKind(SyntaxKind.InKeyword))
        {
            return;
        }

        if (argument.Parent is not ArgumentListSyntax argumentList) return;
        if (argument.FirstAncestorOrSelf<AttributeSyntax>() != null) return;

        var invocation = argumentList.Parent;
        if (invocation == null) return;

        var symbolInfo = model.GetSymbolInfo(invocation);
        IMethodSymbol? method = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        var value = argument.Expression;

        // dynamic 绑定调用（实参为残余 struct）→ SV014 登记，不插。
        if (method == null)
        {
            // Constructor initializers (this(...)/base(...)) and other argument
            // parents are never dynamic invocation sites this branch tracks.
            if (invocation is not InvocationExpressionSyntax actualInvocation)
            {
                return;
            }

            var invocationType = model.GetTypeInfo(actualInvocation).Type;
            var receiverType = model.GetTypeInfo(
                (actualInvocation.Expression as MemberAccessExpressionSyntax)?.Expression
                ?? actualInvocation.Expression).Type;
            if ((invocationType?.TypeKind == TypeKind.Dynamic || receiverType?.TypeKind == TypeKind.Dynamic)
                && ActivePlanForType(model.GetTypeInfo(value).Type) != null)
            {
                _sv014Files.Add(value.SyntaxTree?.FilePath ?? string.Empty);
            }
            return;
        }

        if (IsInsideExpressionTree(argument, model))
        {
            _sv013Files.Add(argument.SyntaxTree?.FilePath ?? string.Empty);
            return;
        }

        var parameter = ResolveParameter(argument, argumentList, method);
        if (parameter == null || parameter.RefKind != RefKind.None) return;
        if (_priorityAnchors.Contains(value)) return;

        // 序列化族泛型汇 Serialize<T>(T)：调用点形参已被推断为实参类型，
        // 须回到方法定义上判定形参是否为类型形参。
        var parameterIndex = method.Parameters.IndexOf(parameter);
        var definitionParameter = parameterIndex >= 0 && method.IsGenericMethod
            ? method.OriginalDefinition.Parameters[parameterIndex]
            : parameter;
        if (definitionParameter.Type is ITypeParameterSymbol
            && IsSerializerFamily(method.ContainingType)
            && ActivePlanForType(model.GetTypeInfo(value).Type) is { } serializedPlan
            && _options_InsertAtBoxingSites())
        {
            AddSite(serializedPlan, SiteKind.Boxing, value, argument);
            return;
        }

        if (TryClassifyConversionSite(value, parameter.Type, model, argument,
                out var kind, out var boxingPlan, method))
        {
            AddSite(boxingPlan!, kind, value, argument);
            return;
        }

        var plan = ActivePlanForType(parameter.Type);
        if (plan != null)
            AddSite(plan, SiteKind.Argument, value, argument);
    }

    // VS-3：防御性接收者 r.Mutate()
    private void VisitDefensiveReceiver(InvocationExpressionSyntax invocation, SemanticModel model)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return;
        if (IsInsideExpressionTree(invocation, model)) return;

        var symbolInfo = model.GetSymbolInfo(invocation);
        var callee = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (callee == null || callee.MethodKind != MethodKind.Ordinary || callee.IsStatic) return;

        var plan = ActivePlanForType(callee.ContainingType);
        if (plan == null) return;
        if (!callee.OriginalDefinition.IsIn(plan.MutatingMethods)) return;

        var receiver = memberAccess.Expression;
        if (IsDirectlyMutableStorage(receiver, model)) return; // C# 原位变异，无需防御副本

        // 接收者非左值 rvalue（调用/new 等）也提升：统一形态（设计 §4.3 VS-3）。
        var statementPosition = invocation.Parent is ExpressionStatementSyntax;
        AddSite(plan, SiteKind.DefensiveReceiver, invocation, invocation,
            isStatementPosition: statementPosition);
    }

    // VS-5 cast：(object)s / (IFoo)s 锚内部表达式；(S)o 拆箱锚 cast 本身（优先级锚点）。
    private void VisitCast(CastExpressionSyntax cast, SemanticModel model)
    {
        if (IsInsideExpressionTree(cast, model)) return;
        if (!_options_InsertAtBoxingSites()) return;

        // GetConversion 对显式 cast 不回填拆箱标志；显式分类才能识别 unboxing。
        var sourceType = model.GetTypeInfo(cast.Expression).Type;
        var targetType = model.GetTypeInfo(cast.Type).Type;
        if (targetType == null) return;
        var conversion = sourceType != null
            ? _compilation.ClassifyConversion(sourceType, targetType)
            : model.GetConversion(cast);

        if (conversion.IsUnboxing)
        {
            // 幂等：已被 .Clone() 消费的拆箱表达式（((S)o).Clone()，含上一轮产物）不重复登记。
            if (ActivePlanForType(targetType) is { } unboxPlan && !IsCloneReceiver(cast, model))
            {
                _priorityAnchors.Add(cast);
                AddSite(unboxPlan, SiteKind.Unbox, cast, cast);
            }
            return;
        }

        var valuePlan = ActivePlanForType(model.GetTypeInfo(cast.Expression).Type);
        if (valuePlan == null) return;

        if (targetType.TypeKind == TypeKind.Interface)
        {
            if (targetType is INamedTypeSymbol namedInterface
                && InterfaceHasMutatingMember(valuePlan, namedInterface))
            {
                AddSite(valuePlan, SiteKind.Interface, cast.Expression, cast);
            }
            return;
        }

        if (IsObjectLike(targetType))
            AddSite(valuePlan, SiteKind.Boxing, cast.Expression, cast);
    }

    /// <summary>
    /// cast 是否已经是无参 <c>Clone()</c> 调用的接收者（允许任意层括号）：
    /// <c>((S)o).Clone()</c>——用户显式克隆或上轮产物，不再重复登记（幂等）。
    /// </summary>
    private static bool IsCloneReceiver(CastExpressionSyntax cast, SemanticModel model)
    {
        SyntaxNode? node = cast;
        while (node.Parent is ParenthesizedExpressionSyntax paren)
            node = paren;

        if (node.Parent is not MemberAccessExpressionSyntax access || access.Expression != node)
            return false;
        if (access.Name is not IdentifierNameSyntax memberName
            || memberName.Identifier.Text != CloneShimGenerator.CloneMethodName)
        {
            return false;
        }
        if (access.Parent is not InvocationExpressionSyntax invocation
            || invocation.Expression != access
            || invocation.ArgumentList.Arguments.Count != 0)
        {
            return false;
        }

        return model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method && !method.IsStatic;
    }

    // VS-5c：o is S x —— x 绑定拆箱副本；标记局部为只读槽（其变异调用由 VS-3 提升，
    // 其流出副本由 VS-1/2 常规站点包裹）。
    private void VisitDeclarationPattern(DeclarationPatternSyntax pattern, SemanticModel model)
    {
        if (!_options_InsertAtBoxingSites()) return;
        if (IsInsideExpressionTree(pattern, model)) return;

        var inputType = pattern.Parent is IsPatternExpressionSyntax isPattern
            ? model.GetTypeInfo(isPattern.Expression).Type
            : null;
        // 仅拆箱路径（源为 object/ValueType/接口）；裸类型模式 s is S（同类型转换）不产生副本差异。
        var patternType = model.GetTypeInfo(pattern.Type).Type;
        if (patternType == null) return;
        var isUnboxing = inputType != null
            && (IsObjectLike(inputType) || inputType.TypeKind == TypeKind.Interface);
        if (!isUnboxing || ActivePlanForType(patternType) == null) return;

        if (pattern.Designation is SingleVariableDesignationSyntax designation
            && model.GetDeclaredSymbol(designation) is ILocalSymbol local)
        {
            _unboxPatternLocals.Add(local);
        }
    }

    /// <summary>
    /// 按目标类型分类值到 object/dynamic/接口的转换站点；返回 false 时调用方按 VS-1/2 普通值站点处理。
    /// </summary>
    private bool TryClassifyConversionSite(
        ExpressionSyntax value,
        ITypeSymbol targetType,
        SemanticModel model,
        SyntaxNode enclosingNode,
        out SiteKind kind,
        out ClonePlan? plan,
        IMethodSymbol? sinkMethod = null)
    {
        kind = default;
        plan = null;

        if (IsInsideExpressionTree(value, model))
        {
            _sv013Files.Add(value.SyntaxTree?.FilePath ?? string.Empty);
            return false;
        }

        var valuePlan = ActivePlanForType(model.GetTypeInfo(value).Type);
        if (valuePlan == null) return false;

        if (targetType.TypeKind == TypeKind.Dynamic)
        {
            _sv014Files.Add(value.SyntaxTree?.FilePath ?? string.Empty);
            return false;
        }

        if (targetType.TypeKind == TypeKind.Interface)
        {
            if (!_options_InsertAtBoxingSites()) return false;
            if (targetType is INamedTypeSymbol namedInterface
                && InterfaceHasMutatingMember(valuePlan, namedInterface))
            {
                kind = SiteKind.Interface;
                plan = valuePlan;
                return true;
            }
            return false;
        }

        if (IsObjectLike(targetType))
            {
                if (!_options_InsertAtBoxingSites()) return false;
                if (sinkMethod != null && IsHarmlessSink(sinkMethod))
                    return false;
                kind = SiteKind.Boxing;
                plan = valuePlan;
                return true;
            }

        return false;
    }

    private bool _options_InsertAtBoxingSites() => _analysis.Options.InsertAtBoxingSites;

    private bool InterfaceHasMutatingMember(ClonePlan plan, INamedTypeSymbol interfaceType)
    {
        var key = (plan, interfaceType.OriginalDefinition);
        if (_mutatingInterfaceCache.TryGetValue(key, out var cached)) return cached;

        var result = false;
        foreach (var member in interfaceType.OriginalDefinition.GetMembers())
        {
            var implementation = plan.Symbol.FindImplementationForInterfaceMember(member);
            switch (implementation)
            {
                case IMethodSymbol method when plan.MutatingMethods.Any(m =>
                        SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition)):
                    result = true;
                    break;
                case IPropertySymbol property when plan.MutableProperties.Contains(property):
                    result = true;
                    break;
            }
            if (result) break;
        }

        _mutatingInterfaceCache[key] = result;
        return result;
    }

    /// <summary>
    /// 接收者是否为「可直接原位变异」的存储（普通局部/值参/可写字段/set 可写属性/数组元素槽/
    /// foreach 以外的可写槽）。in 参、readonly 字段、get-only 属性、foreach 变量、
    /// 只读索引器、rvalue 均需防御性副本。
    /// </summary>
    private bool IsDirectlyMutableStorage(ExpressionSyntax receiver, SemanticModel model)
    {
        var current = receiver;
        while (current is ParenthesizedExpressionSyntax paren)
            current = paren.Expression;

        var symbol = model.GetSymbolInfo(current).Symbol;
        switch (symbol)
        {
            case IParameterSymbol parameter:
                return parameter.RefKind == RefKind.None && !parameter.IsThis;
            case IFieldSymbol { IsReadOnly: false }:
                return true;
            case IPropertySymbol property when !property.IsIndexer:
                return property.SetMethod is { IsInitOnly: false } && !property.ReturnsByRefReadonly;
            case ILocalSymbol local when local.RefKind == RefKind.None:
                // foreach 迭代变量与 is-pattern 拆箱绑定都是只读副本槽。
                return !IsForEachVariable(local, current)
                    && !_unboxPatternLocals.Contains(local);
        }

        if (current is ElementAccessExpressionSyntax elementAccess)
        {
            var receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
            if (receiverType is IArrayTypeSymbol) return true; // VS-4c：数组槽直改合法
            if (model.GetSymbolInfo(elementAccess).Symbol is IPropertySymbol indexer)
                return indexer.SetMethod is { IsInitOnly: false };
        }

        return false;
    }

    private static bool IsForEachVariable(ILocalSymbol local, SyntaxNode usage)
    {
        // foreach 迭代变量的声明语法引用直接落在 ForEachStatementSyntax（无 VariableDeclarator）。
        var declaration = local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        return declaration is ForEachStatementSyntax
            || declaration is { Parent: VariableDeclarationSyntax { Parent: ForEachStatementSyntax } };
    }

    private static bool IsObjectLike(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType;

    private static bool ValueFlowIsFresh(ExpressionSyntax expression) =>
        ValueCopyFacts.IsFreshValue(expression);

    private static bool IsArrayReceiver(ElementAccessExpressionSyntax elementAccess, SemanticModel model)
    {
        var receiverType = model.GetTypeInfo(elementAccess.Expression).Type;
        return receiverType is IArrayTypeSymbol or IPointerTypeSymbol;
    }

    /// <summary>无害格式化/日志汇（口径复制 BoxingReachabilityAnalyzer.IsHarmlessFormattingSink）。</summary>
    private static bool IsHarmlessSink(IMethodSymbol method)
    {
        var containingType = method.ContainingType?.OriginalDefinition;
        if (containingType == null) return false;
        var containing = containingType.ToDisplayString();
        var name = method.Name;

        // string 的 ToDisplayString() 走 C# 关键字风格（"string"），用 SpecialType 兜底。
        if (containingType.SpecialType == SpecialType.System_String)
            return name is "Format" or "Concat" or "Join";

        var harmless = containing switch
        {
            "System.Console" => name.StartsWith("Write", StringComparison.Ordinal),
            "System.Text.StringBuilder" =>
                name is "Append" or "AppendLine" or "AppendFormat" or "Insert",
            "System.IO.TextWriter" => name.StartsWith("Write", StringComparison.Ordinal),
            "System.Diagnostics.Debug" or "System.Diagnostics.Trace" => true,
            "object" => name is "Equals" or "ReferenceEquals" or "GetHashCode" or "ToString",
            _ => false,
        };
        return harmless;
    }

    private static bool IsSerializerFamily(ISymbol? type)
    {
        var name = type?.Name ?? string.Empty;
        return name.IndexOf("Serializ", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Deserializ", StringComparison.OrdinalIgnoreCase) >= 0
            || name.Equals("Mapper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideExpressionTree(SyntaxNode node, SemanticModel model)
    {
        var lambda = node.FirstAncestorOrSelf<LambdaExpressionSyntax>();
        if (lambda == null) return false;
        var converted = model.GetTypeInfo(lambda).ConvertedType;
        return converted is INamedTypeSymbol named
            && named.OriginalDefinition.ToDisplayString()
                == "System.Linq.Expressions.Expression<TDelegate>";
    }

    private static IParameterSymbol? ResolveParameter(
        ArgumentSyntax argument, ArgumentListSyntax argumentList, IMethodSymbol method)
    {
        var parameters = method.Parameters;
        if (parameters.Length == 0) return null;

        if (argument.NameColon?.Name.Identifier.ValueText is { } name)
            return parameters.FirstOrDefault(p => p.Name == name);

        var index = argumentList.Arguments
            .Where(a => a.NameColon == null)
            .TakeWhile(a => a != argument)
            .Count();

        if (index < parameters.Length) return parameters[index];
        // params 展开形调用（string.Format("{0}", s)）实参归到 params 数组形参。
        if (parameters.Length > 0 && parameters[^1].IsParams)
            return parameters[^1];
        return null;
    }

    private void AddSite(
        ClonePlan plan,
        SiteKind kind,
        SyntaxNode anchor,
        SyntaxNode enclosingNode,
        bool isStatementPosition = true)
    {
        var filePath = anchor.SyntaxTree?.FilePath ?? string.Empty;
        var typeName = enclosingNode.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault()?.Identifier.Text;
        var memberName = enclosingNode.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
            .FirstOrDefault()?.GetMemberNameString();

        plan.Sites.Add(new ValueCopySite
        {
            Kind = kind,
            Plan = plan,
            Anchor = anchor,
            FilePath = filePath,
            Touch = new PlanTouch(filePath, typeName, memberName),
            IsStatementPosition = isStatementPosition,
        });
    }

    private sealed class PlanInterfaceComparer : IEqualityComparer<(ClonePlan Plan, INamedTypeSymbol Interface)>
    {
        public bool Equals((ClonePlan Plan, INamedTypeSymbol Interface) x,
            (ClonePlan Plan, INamedTypeSymbol Interface) y)
            => ReferenceEquals(x.Plan, y.Plan)
                && SymbolEqualityComparer.Default.Equals(x.Interface, y.Interface);

        public int GetHashCode((ClonePlan Plan, INamedTypeSymbol Interface) obj)
            => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Plan),
                SymbolEqualityComparer.Default.GetHashCode(obj.Interface));
    }
}

internal static class StructValueSemanticsSymbolSetExtensions
{
    public static bool IsIn(this IMethodSymbol method, HashSet<IMethodSymbol> set)
        => set.Any(m => SymbolEqualityComparer.Default.Equals(m.OriginalDefinition, method.OriginalDefinition));
}
