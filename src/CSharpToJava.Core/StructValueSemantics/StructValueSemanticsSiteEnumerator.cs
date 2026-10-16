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

    /// <summary>plan × 接口定义 → 接口是否含命中变异闭包的成员（缓存）。</summary>
    private readonly Dictionary<(ClonePlan Plan, INamedTypeSymbol Interface), bool> _mutatingInterfaceCache =
        new(new PlanInterfaceComparer());

    /// <summary>调用点节点 → 已解析的被调方（同一调用点的多个实参共享一次 GetSymbolInfo）。</summary>
    private readonly Dictionary<SyntaxNode, IMethodSymbol?> _invocationSymbols =
        new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<string> _sv013Files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _sv014Files = new(StringComparer.OrdinalIgnoreCase);

    // 单树归并面：站点/跳过文件按“首次出现次序”累积，父实例按树序合并回写。
    private readonly List<ValueCopySite> _pendingSites = new();
    private readonly List<string> _sv013Order = new();
    private readonly List<string> _sv014Order = new();
    private int _uninitializedFields;

    /// <summary>
    /// 全部活跃 plan 的变异方法名并集。VS-3 只对「接收者类型的变异实例方法」建站点，
    /// 该方法名必落在本集合内；成员访问名不在集合内时不可能命中任何 plan.MutatingMethods，
    /// 故可在 GetSymbolInfo 之前短路（名字无别名机制，判定精确到名字层面为必要条件）。
    /// </summary>
    private readonly HashSet<string> _mutatingMethodNames = new(StringComparer.Ordinal);

    public StructValueSemanticsSiteEnumerator(StructValueSemanticsAnalysis analysis)
    {
        _analysis = analysis;
        _compilation = analysis.Compilation;
        _plans = analysis.PlansBySymbol;
        foreach (var plan in _plans.Values)
        {
            if (!plan.IsActive) continue;
            foreach (var method in plan.MutatingMethods)
                _mutatingMethodNames.Add(method.Name);
        }
    }

    /// <summary>
    /// Phase 1c 入口：各语法树彼此独立（站点/优先级锚点/SV013·SV014 均为树内事实，
    /// 跨树仅经引用相等匹配故不会互相命中），按树并行枚举后严格按
    /// <see cref="Compilation.SyntaxTrees"/> 顺序归并回写 plan.Sites 与跳过文件集，
    /// 使插入次序与串行逐树枚举逐位一致（输出逐字节守恒）。
    /// </summary>
    public void Enumerate()
    {
        var trees = _compilation.SyntaxTrees.ToList();
        var results = new TreeEnumerationResult[trees.Count];
        if (trees.Count == 0) return;

        System.Threading.Tasks.Parallel.For(0, trees.Count, i =>
        {
            results[i] = new StructValueSemanticsSiteEnumerator(_analysis).EnumerateTree(trees[i]);
        });

        foreach (var result in results)
        {
            _pendingSites.AddRange(result.Sites);
            foreach (var file in result.Sv013Files) _sv013Files.Add(file);
            foreach (var file in result.Sv014Files) _sv014Files.Add(file);
            _analysis.PlanningStatistics.UninitializedStructFields += result.UninitializedFields;
        }
        // 按树序（= 串行枚举序）回写站点，plan.Sites 次序与串行完全一致。
        foreach (var site in _pendingSites)
            site.Plan.Sites.Add(site);

        EmitSkipInfo(StructValueSemanticsDiagnosticCode.SV013, _sv013Files,
            "value-copy sites inside expression-tree lambdas were skipped.");
        EmitSkipInfo(StructValueSemanticsDiagnosticCode.SV014, _sv014Files,
            "dynamically bound value-copy sites were skipped.");
    }

    /// <summary>单树枚举（纯树内状态），返回按节点序累积的站点与跳过文件。</summary>
    private TreeEnumerationResult EnumerateTree(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        var model = _compilation.GetSemanticModel(tree);
        var allNodes = root.DescendantNodes().ToList();

        // pass 1：VS-5 cast（(object)s / (IFoo)s / (S)o）。
        foreach (var node in allNodes.OfType<CastExpressionSyntax>())
            VisitCast(node, model);

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

        return new TreeEnumerationResult(_pendingSites, _sv013Order, _sv014Order, _uninitializedFields);
    }

    private sealed record TreeEnumerationResult(
        List<ValueCopySite> Sites,
        List<string> Sv013Files,
        List<string> Sv014Files,
        int UninitializedFields);

    private void MarkSv013(string file)
    {
        if (_sv013Files.Add(file)) _sv013Order.Add(file);
    }

    private void MarkSv014(string file)
    {
        if (_sv014Files.Add(file)) _sv014Order.Add(file);
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
        // __cs2jVc / this 持有者 __cs2jTc / this 赋值展开临时 __cs2jTa）重放时不是新
        // 站点——链式临时故意持有未克隆的共享右值，各目标槽已在展开语句中逐个 Clone；
        // 持有者按 copy-in 语义刻意持有未克隆的 this 快照（出口统一回写）；this 赋值
        // 展开临时在块内即死，字段所有权直接转移给 this。再对其初始化器插 clone 会重复。
        var declaratorName = declarator.Identifier.Text;
        if (declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.ChainedAssignTempPrefix,
                StringComparison.Ordinal)
            || declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.DefensiveCopyTempPrefix,
                StringComparison.Ordinal)
            || declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix,
                StringComparison.Ordinal)
            || declaratorName.StartsWith(StructValueSemanticsSyntaxFacts.ThisAssignTempPrefix,
                StringComparison.Ordinal))
        {
            return;
        }

        // 语法预筛：显式声明为「object 除外的预定义类型关键字」（int/string/bool/…）的槽位
        // 不可能是活跃 struct，也不可能是 object/ValueType/接口/dynamic 目标，任何站点分支
        // 都不成立（VS-6 计数同理）；据此在 GetDeclaredSymbol 之前短路。
        if (declarator.Parent is VariableDeclarationSyntax { Type: PredefinedTypeSyntax predefined }
            && !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword))
        {
            return;
        }

        if (declarator.Initializer?.Value is not { } value)
        {
            // VS-6：无初始化器的 struct 类型实例字段——零值/默认构造语义另由既有路径负责，仅登记。
            if (model.GetDeclaredSymbol(declarator) is IFieldSymbol { IsStatic: false } uninitializedField
                && ActivePlanForType(uninitializedField.Type) != null)
            {
                _uninitializedFields++;
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

        if (TryClassifyRelevantValueCopySite(value, declaredType, model, declarator,
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

        // 幂等：SV040-d 生成的出口回写 this = __cs2jTcN：
        // 持有者按 copy-out 语义整体回写，不得再对 RHS 插 Clone。
        if (UnwrapParens(assignment.Left) is ThisExpressionSyntax
            && UnwrapParens(assignment.Right) is IdentifierNameSyntax copyOut
            && copyOut.Identifier.ValueText.StartsWith(
                StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, StringComparison.Ordinal))
        {
            return;
        }

        // 幂等：SV041 生成的字段式展开 this.f = __cs2jTaN.f（含自动属性形态）：
        // 临时在展开块内即死，字段引用所有权已转移，不得再对 RHS 插 Clone。
        if (UnwrapParens(assignment.Left) is MemberAccessExpressionSyntax
                { Expression: ThisExpressionSyntax }
            && RootsAtGeneratedThisAssignTemp(assignment.Right))
        {
            return;
        }

        if (IsInsideExpressionTree(assignment, model)) return;

        // ref-returning 方法/属性/索引器槽赋值是别名写入，属 SV040/SV042 保守族；不按值站点处理。
        if (assignment.Left is InvocationExpressionSyntax) return;
        var leftSymbol = model.GetSymbolInfo(assignment.Left).Symbol;
        if (leftSymbol is ILocalSymbol { RefKind: not RefKind.None }) return;
        if (leftSymbol is IPropertySymbol { ReturnsByRef: true }) return;

        var lhsType = SymbolTypeOf(leftSymbol) ?? model.GetTypeInfo(assignment.Left).Type;
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

        if (TryClassifyRelevantValueCopySite(value, lhsType, model, assignment,
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

        if (TryClassifyRelevantValueCopySite(value, enclosing.ReturnType, model, ret,
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

        // 同一被调方在一次枚举里会被每个实参各问一次；按 invocation 节点记忆一次解析结果，
        // 把 GetSymbolInfo 调用数从「实参数」降到「调用点数」，且结果逐字相同。
        if (!_invocationSymbols.TryGetValue(invocation, out var method))
        {
            var symbolInfo = model.GetSymbolInfo(invocation);
            method = symbolInfo.Symbol as IMethodSymbol
                ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            _invocationSymbols[invocation] = method;
        }

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

            // 先判最便宜的实参类型（本分支登记 SV014 的必要条件），再取被调方/接收者类型；
            // 与「(调用/接收者动态) && 实参为活跃 struct」的合取等价，但多数实参非 struct 时
            // 可省去两次逐节点类型查询。
            if (ActivePlanForType(model.GetTypeInfo(value).Type) == null) return;

            var invocationType = model.GetTypeInfo(actualInvocation).Type;
            var receiverType = model.GetTypeInfo(
                (actualInvocation.Expression as MemberAccessExpressionSyntax)?.Expression
                ?? actualInvocation.Expression).Type;
            if (invocationType?.TypeKind == TypeKind.Dynamic || receiverType?.TypeKind == TypeKind.Dynamic)
            {
                MarkSv014(value.SyntaxTree?.FilePath ?? string.Empty);
            }
            return;
        }

        if (IsInsideExpressionTree(argument, model))
        {
            MarkSv013(argument.SyntaxTree?.FilePath ?? string.Empty);
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

        if (TryClassifyRelevantValueCopySite(value, parameter.Type, model, argument,
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
        // 名预筛：调用名不在活跃 plan 变异方法名并集内 → 必不命中任何 plan.MutatingMethods，
        // 直接跳过最贵的 GetSymbolInfo（本 pass 绝大多数调用与本 struct 无关）。
        if (memberAccess.Name is not SimpleNameSyntax simpleName
            || !_mutatingMethodNames.Contains(simpleName.Identifier.Text))
        {
            return;
        }
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

        // 目标类型取 cast 表达式自身类型（== cast.Type 的目标类型），避免对 TypeSyntax 走
        // model.GetTypeInfo(cast.Type) 的病态慢路径（实测 ~6ms/次，全树 cast 累计 >100s）；
        // 表达式类型信息在 SemanticModel 中按表达式缓存，成本 ~µs 级。
        // GetConversion 对显式 cast 不回填拆箱标志；显式分类才能识别 unboxing。
        //
        // 语法预筛（第一级）：目标类型写的是基元关键字（int/string/bool/...）时不可能是
        // 活跃 struct 或接口，任何分支都不会 AddSite（也不触动优先级锚点），据此在逐节点
        // 类型查询之前短路——语料中大量 cast 属基元/指针转换族。注意 object 同属预定义类型
        // 关键字，却正是装箱站点目标（object/ValueType），必须排除在本次短路之外。
        if (cast.Type is PredefinedTypeSyntax predefined
            && !predefined.Keyword.IsKind(SyntaxKind.ObjectKeyword))
        {
            return;
        }

        var targetType = model.GetTypeInfo(cast).Type;
        if (targetType == null) return;

        // 目标类型相关性（第二级）：cast 站点只有三种形态——拆箱（目标须为活跃 struct）、
        // 接口站点（目标须为接口）、装箱站点（目标须为 object/ValueType）。目标既非活跃
        // struct、又非接口/object-like 时任何分支都不建站点，此时连带 value 的语义查询与
        // ClassifyConversion 一并省去（同一时刻 valuePlan 即便非空也无对应分支）。
        var targetPlan = ActivePlanForType(targetType);
        if (targetPlan == null
            && targetType.TypeKind != TypeKind.Interface
            && !IsObjectLike(targetType))
        {
            return;
        }

        var sourceType = model.GetTypeInfo(cast.Expression).Type;

        if (targetPlan != null)
        {
            // 活跃 struct 目标：唯一分支是拆箱（目标为值类型，不可能是接口/object-like）。
            var conversion = sourceType != null
                ? _compilation.ClassifyConversion(sourceType, targetType)
                : model.GetConversion(cast);
            if (conversion.IsUnboxing && !IsCloneReceiver(cast, model))
            {
                _priorityAnchors.Add(cast);
                AddSite(targetPlan, SiteKind.Unbox, cast, cast);
            }
            return;
        }

        // 目标为接口/object-like：装箱/接口站点，要求源类型为本轮活跃 struct。
        var valuePlan = ActivePlanForType(sourceType);
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

    /// <summary>
    /// 具名存储符号的静态类型（局部/值参/字段/属性）。赋值左槽为这些符号时，
    /// <c>GetSymbolInfo(Left).Symbol.Type</c> 与 <c>GetTypeInfo(Left).Type</c> 是同一类型
    /// （成员访问返回已构造成员符号），据此省去一次 <c>GetTypeInfo</c> 逐节点查询；
    /// 其余左槽形态（数组元素、指针解引用、圆括号复合槽等绑定不出具名符号）仍走语义查询。
    /// </summary>
    private static ITypeSymbol? SymbolTypeOf(ISymbol? symbol) => symbol switch
    {
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        IFieldSymbol field => field.Type,
        IPropertySymbol property => property.Type,
        _ => null,
    };

    /// <summary>
    /// 目标类型是否可能承载装箱/接口/动态转换站点：dynamic / interface / object·ValueType。
    /// 其余目标（含残余 struct 自身与基元/BCL）下 <see cref="TryClassifyConversionSite"/>
    /// 恒返回 false，唯一副作用是值落于表达式树时登记 SV013——故仅保留该副作用，
    /// 省去对 value 的 <c>GetTypeInfo</c>（全树最贵的逐节点语义查询之一）。
    /// </summary>
    private static bool TargetMayHostConversionSite(ITypeSymbol targetType) =>
        targetType.TypeKind is TypeKind.Dynamic or TypeKind.Interface || IsObjectLike(targetType);

    /// <summary>
    /// <see cref="TryClassifyConversionSite"/> 的等价入口：仅在目标类型可能产生转换站点时
    /// 才执行完整分类（含 value 的语义查询）；否则只补做表达式树跳过登记（SV013），
    /// 其余判定交回调用方按其 VS-1/2 普通站点路径处理。
    /// </summary>
    private bool TryClassifyRelevantValueCopySite(
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
        if (TargetMayHostConversionSite(targetType))
        {
            return TryClassifyConversionSite(
                value, targetType, model, enclosingNode, out kind, out plan, sinkMethod);
        }

        if (IsInsideExpressionTree(value, model))
            MarkSv013(value.SyntaxTree?.FilePath ?? string.Empty);
        return false;
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
            MarkSv013(value.SyntaxTree?.FilePath ?? string.Empty);
            return false;
        }

        var valuePlan = ActivePlanForType(model.GetTypeInfo(value).Type);
        if (valuePlan == null) return false;

        if (targetType.TypeKind == TypeKind.Dynamic)
        {
            MarkSv014(value.SyntaxTree?.FilePath ?? string.Empty);
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
                // foreach 迭代变量是只读副本槽（C# 禁止赋值，变异不回写源元素）；
                // is-pattern 声明变量是普通可写局部（拆箱已产生独立副本，对其变异
                // 必须就地生效，见 VS-3 基线第 6 行），按普通局部直改处理。
                return !IsForEachVariable(local, current);
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

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;
        return expression;
    }

    /// <summary>
    /// 表达式链（成员/元素访问）的根是否为本 pass 生成的 this 赋值展开临时
    /// <c>__cs2jTaN</c>（幂等豁免字段式展开产物）。
    /// </summary>
    private static bool RootsAtGeneratedThisAssignTemp(ExpressionSyntax expression)
    {
        var root = UnwrapParens(expression);
        while (root is MemberAccessExpressionSyntax memberAccess)
            root = UnwrapParens(memberAccess.Expression);
        while (root is ElementAccessExpressionSyntax elementAccess)
            root = UnwrapParens(elementAccess.Expression);
        return root is IdentifierNameSyntax identifier
            && identifier.Identifier.ValueText.StartsWith(
                StructValueSemanticsSyntaxFacts.ThisAssignTempPrefix, StringComparison.Ordinal);
    }

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

        // 站点先累积在树内顺序表里，待父实例按树序统一回写 plan.Sites（并行的确定性归并）。
        _pendingSites.Add(new ValueCopySite
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
