using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSharpToJava.Core.DynamicDesugar;

/// <summary>
/// P1 证明通过的 dynamic 局部声明 → 证明类型 T（设计 §4.4/§4.5）。
/// M2 后续 DynamicSiteRewriter 的公共输入：仅把 <see cref="Declarator"/> 的声明类型
/// 语法具体化为 <see cref="Type"/>，使用点经重编译自动静态重绑。
/// 节点为清单站点对应的声明语法节点之一：
/// <see cref="VariableDeclaratorSyntax"/>（显式 dynamic / var 推断 dynamic）、
/// <see cref="ForEachStatementSyntax"/>、<see cref="CatchDeclarationSyntax"/>、
/// <see cref="SingleVariableDesignationSyntax"/>（解构/foreach 解构/out var 分量）。
/// </summary>
public sealed record ProvenDeclaration(SyntaxNode Declarator, ITypeSymbol Type);

/// <summary>单个 DynamicLocalDecl 站点的证明结论。</summary>
public sealed class FlowLocalClassification
{
    /// <summary>对应的清单站点（引用相等）。</summary>
    public required DynamicSiteRecord Site { get; init; }

    /// <summary>站点声明节点（同 <see cref="ProvenDeclaration.Declarator"/> 形态）。</summary>
    public required SyntaxNode Declarator { get; init; }

    /// <summary>P1 可证 → ProvenP1；否则 Unprovable（本证明器不重判 P2）。</summary>
    public required DynamicProvability Provability { get; init; }

    /// <summary>不可证具名原因（可证时为 null）。</summary>
    public required DynamicUnprovableReason? UnprovableReason { get; init; }

    /// <summary>证明所得具体类型 T（仅 ProvenP1 非 null）。</summary>
    public ITypeSymbol? ProvenType { get; init; }

    /// <summary>诊断/报告消息（DS001 或 DS010 句式，已具名原因与建议动作）。</summary>
    public required string Message { get; init; }

    /// <summary>证明类型 T 的显示名（仅 ProvenP1 非 null，回写 OriginType 用）。</summary>
    public string? ProvenTypeDisplay { get; init; }
}

/// <summary>整树一次的证明结果。</summary>
public sealed class FlowTypeProof
{
    public required IReadOnlyList<FlowLocalClassification> Classifications { get; init; }

    /// <summary>P1 通过集合（清单顺序，DynamicSiteRewriter M2 输入）。</summary>
    public required IReadOnlyList<ProvenDeclaration> ProvenDeclarations { get; init; }
}

/// <summary>
/// P1 局部具体化证明器（设计 §4.4，D2/D11）。保守的语法+语义混合分析，宁假阳：
/// ①赋值并集（文本收集声明方法体内全部赋值点，含嵌套匿名函数/本地函数，不做 CFG
/// 存活分析）；②逃逸（ref/out/in、捕获、fixed/lock/using/await 特殊位置）；
/// ③sealed 校验。只用公共 Roslyn API：GetTypeInfo/GetSymbolInfo/GetDeclaredSymbol，
/// 禁止 Microsoft.CodeAnalysis.FlowAnalysis。
/// </summary>
internal static class FlowTypeProver
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static FlowTypeProof Classify(
        Compilation compilation,
        DynamicSiteInventory inventory,
        DynamicDesugarOptions options)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);

        var modelsByPath = compilation.SyntaxTrees
            .ToDictionary(tree => tree.FilePath ?? string.Empty, tree => (Tree: tree, Root: tree.GetRoot()));

        var classifications = new List<FlowLocalClassification>();
        foreach (var site in inventory.Sites)
        {
            if (site.Kind != DynamicSiteKind.DynamicLocalDecl
                || !modelsByPath.TryGetValue(site.FilePath, out var entry))
            {
                continue;
            }

            var model = compilation.GetSemanticModel(entry.Tree);
            var span = new TextSpan(site.SpanStart, site.SpanEnd - site.SpanStart);
            var node = entry.Root.FindNode(span, getInnermostNodeForTie: true);

            classifications.Add(ClassifyLocal(compilation, model, site, node));
        }

        return new FlowTypeProof
        {
            Classifications = classifications,
            ProvenDeclarations = classifications
                .Where(c => c is { Provability: DynamicProvability.ProvenP1, ProvenType: not null })
                .Select(c => new ProvenDeclaration(c.Declarator, c.ProvenType!))
                .ToList(),
        };
    }

    // ---------- §4.4 主流程：赋值并集 → 逃逸 → sealed ----------

    private static FlowLocalClassification ClassifyLocal(
        Compilation compilation,
        SemanticModel model,
        DynamicSiteRecord site,
        SyntaxNode node)
    {
        // 清单（§4.3）保证候选为 TypeKind.Dynamic 的 ILocalSymbol；拿不到符号属于语义
        // 模型异常/基线错误树，保守按 unresolved symbol 不可证（宁假阳，R10）。
        if (model.GetDeclaredSymbol(node) is not ILocalSymbol local
            || local.Type.TypeKind != TypeKind.Dynamic)
        {
            return Fail(site, node, localName: site.BoundSymbolName ?? node.ToString(),
                DynamicUnprovableReason.UnresolvedSymbol);
        }

        var owner = FindOwner(node);
        var body = owner == null ? null : OwnerBody(owner);
        if (body == null)
        {
            // 找不到可文本收集的声明方法体（异常形态）：无赋值点可证 → unresolved symbol。
            return Fail(site, node, local.Name, DynamicUnprovableReason.UnresolvedSymbol);
        }

        // ① 赋值并集
        var points = CollectAssignmentPoints(compilation, model, node, body, local);
        var (unionReason, provenType) = EvaluateAssignmentUnion(model, points);
        if (unionReason != null)
        {
            return Fail(site, node, local.Name, unionReason.Value);
        }

        // ② 逃逸（ref/out/in 先于捕获/特殊位置）
        if (HasByRefEscape(body, model, local))
        {
            return Fail(site, node, local.Name, DynamicUnprovableReason.ByRefEscape);
        }

        if (IsCapturedOrSpecialPosition(body, node, model, local))
        {
            return Fail(site, node, local.Name, DynamicUnprovableReason.Captured);
        }

        // ③ sealed 校验
        if (!IsSealedConcrete(provenType!))
        {
            return Fail(site, node, local.Name, DynamicUnprovableReason.SealedNotSatisfied);
        }

        // ④ 通过：ProvenDeclaration(node, T)
        var display = provenType!.ToDisplayString();
        return new FlowLocalClassification
        {
            Site = site,
            Declarator = node,
            Provability = DynamicProvability.ProvenP1,
            UnprovableReason = null,
            ProvenType = provenType,
            ProvenTypeDisplay = display,
            Message = FlowMessages.Proven(local.Name, Text(node), display),
        };
    }

    // ---------- ① 赋值点收集（文本并集，含嵌套函数/lambda 内赋值） ----------

    /// <summary>
    /// 赋值点携带一个待判定类型：<see cref="PreResolvedType"/> 非 null 表示类型已由
    /// 语法位置解析（++/-- 结果、foreach 元素、解构分量、解构赋值分量）；否则对
    /// <see cref="ValueExpression"/> 取 GetTypeInfo().Type ?? ConvertedType。
    /// </summary>
    private readonly record struct AssignmentPoint(SyntaxNode PointNode, ExpressionSyntax? ValueExpression, ITypeSymbol? PreResolvedType);

    private static List<AssignmentPoint> CollectAssignmentPoints(
        Compilation compilation,
        SemanticModel model,
        SyntaxNode candidate,
        SyntaxNode body,
        ILocalSymbol local)
    {
        var points = new List<AssignmentPoint>();

        // 声明形态自带的初始化赋值点。
        switch (candidate)
        {
            case VariableDeclaratorSyntax { Initializer.Value: { } initializer } declaratorWithInit:
                points.Add(new AssignmentPoint(
                    initializer, initializer,
                    ResolveFlowingType(compilation, model, initializer)));

                // 多声明符共享一个类型语法（dynamic a = .., b = ..;）：兄弟声明符的
                // 初始化器同样是该共享类型的流入点——一个类型位不可能承载两个 T，
                // 故按声明（而非按变量）求并集：异 T 时每个分量都得 MultipleAssignmentTypes，
                // DynamicSiteRewriter 才能整体保留该声明（最小调整，仅本分支）。
                if (declaratorWithInit.Parent is VariableDeclarationSyntax sharedDeclaration)
                {
                    foreach (var sibling in sharedDeclaration.Variables)
                    {
                        if (!ReferenceEquals(sibling, declaratorWithInit)
                            && sibling.Initializer?.Value is { } siblingInitializer)
                        {
                            points.Add(new AssignmentPoint(
                                siblingInitializer, siblingInitializer,
                                ResolveFlowingType(compilation, model, siblingInitializer)));
                        }
                    }
                }

                break;

            case ForEachStatementSyntax forEach:
                // foreach 迭代：每轮由集合元素类型赋值。
                var collectionType = ExpressionType(model, forEach.Expression);
                points.Add(new AssignmentPoint(forEach, null,
                    CollectionElementType(compilation, collectionType)));
                break;

            case SingleVariableDesignationSyntax designation:
                // 解构声明 / foreach 解构 / out var 分量的声明期赋值。
                points.Add(new AssignmentPoint(designation, null,
                    DesignationDeclarationType(compilation, model, designation)));
                break;
        }

        // 声明方法体内的文本赋值（含嵌套匿名函数/本地函数子树——文本全收，
        // 跨边界写入不做存活分析，逃逸由步骤②独立判定）。
        foreach (var assignment in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is IdentifierNameSyntax identifier
                && IsReferenceTo(model, identifier, local))
            {
                // 简单赋值取右值类型；复合赋值（d += x）同样取右值静态类型——
                // 复合操作整体动态与否由 DS015 操作点覆盖，并集只关心新流入的右值 T。
                points.Add(new AssignmentPoint(
                    assignment,
                    assignment.Right,
                    ResolveFlowingType(compilation, model, assignment.Right)));
            }
            else if (assignment.Left is TupleExpressionSyntax tuple)
            {
                // 解构赋值 (d, x) = rhs：按分量位置从右值元组钻取类型。
                var rhsType = ExpressionType(model, assignment.Right);
                AddTupleTargetPoints(model, tuple, rhsType, local, assignment, new List<int>(), points);
            }
        }

        // ++/--：无独立右值表达式，取一元节点自身的结果类型；动态局部上该运算整体
        // 晚绑定（结果 Dynamic），保守计为 dynamic 赋值点（宁假阳）。
        foreach (var unary in body.DescendantNodes())
        {
            ExpressionSyntax? operand = unary switch
            {
                PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreIncrementExpression } prefix => prefix.Operand,
                PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PreDecrementExpression } prefix => prefix.Operand,
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostIncrementExpression } postfix => postfix.Operand,
                PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.PostDecrementExpression } postfix => postfix.Operand,
                _ => null,
            };
            if (operand == null)
            {
                continue;
            }

            if (operand.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => IsReferenceTo(model, id, local)))
            {
                var info = model.GetTypeInfo(unary);
                points.Add(new AssignmentPoint(unary, null, info.Type ?? info.ConvertedType));
            }
        }

        return points;
    }

    /// <summary>
    /// 赋值并集判定：任一 Dynamic → DynamicAssignment；Error/null/不可解析 →
    /// UnresolvedSymbol；两个及以上不同静态类型（含派生类、int/int? 可空差异，不合并
    /// 共同基类）→ MultipleAssignmentTypes；零赋值点 → UnresolvedSymbol（宁假阳：
    /// 文本上无法证明来源，即使 C# 明确赋值分析能在部分路径证明有值）。
    /// </summary>
    private static (DynamicUnprovableReason? Reason, ITypeSymbol? Type) EvaluateAssignmentUnion(
        SemanticModel model,
        IReadOnlyList<AssignmentPoint> points)
    {
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.IncludeNullability);
        var sawDynamic = false;
        var sawUnresolved = false;

        foreach (var point in points)
        {
            ITypeSymbol? type;
            if (point.PreResolvedType != null)
            {
                type = point.PreResolvedType;
            }
            else if (point.ValueExpression != null)
            {
                var info = model.GetTypeInfo(point.ValueExpression);
                // 基线错误节点：Type 或 ConvertedType 任一为 Error 即按不可解析
                // （不被 dynamic 转换目标覆盖，R10）。
                if (info.Type?.TypeKind == TypeKind.Error
                    || (info.Type == null && info.ConvertedType?.TypeKind == TypeKind.Error))
                {
                    sawUnresolved = true;
                    continue;
                }

                type = info.Type ?? info.ConvertedType;
            }
            else
            {
                type = null;
            }

            if (type == null || type.TypeKind == TypeKind.Error)
            {
                sawUnresolved = true;
                continue;
            }

            if (type.TypeKind == TypeKind.Dynamic)
            {
                sawDynamic = true;
                continue;
            }

            seen.Add(type);
        }

        if (sawDynamic)
        {
            return (DynamicUnprovableReason.DynamicAssignment, null);
        }

        if (sawUnresolved)
        {
            return (DynamicUnprovableReason.UnresolvedSymbol, null);
        }

        if (points.Count == 0 || seen.Count == 0)
        {
            // 零赋值点（如 catch 变量、dynamic d; 无后续赋值）：文本无来源可证。
            return (DynamicUnprovableReason.UnresolvedSymbol, null);
        }

        if (seen.Count > 1)
        {
            return (DynamicUnprovableReason.MultipleAssignmentTypes, null);
        }

        return (null, seen.Single());
    }

    private static void AddTupleTargetPoints(
        SemanticModel model,
        TupleExpressionSyntax tuple,
        ITypeSymbol? rhsTupleType,
        ILocalSymbol local,
        AssignmentExpressionSyntax assignment,
        List<int> path,
        List<AssignmentPoint> points)
    {
        for (var i = 0; i < tuple.Arguments.Count; i++)
        {
            var nextPath = new List<int>(path) { i };
            switch (tuple.Arguments[i].Expression)
            {
                case TupleExpressionSyntax nested:
                    AddTupleTargetPoints(model, nested, rhsTupleType, local, assignment, nextPath, points);
                    break;

                case IdentifierNameSyntax identifier when IsReferenceTo(model, identifier, local):
                    points.Add(new AssignmentPoint(assignment, null, DrillTupleType(rhsTupleType, nextPath)));
                    break;
            }
        }
    }

    /// <summary>
    /// 解构声明分量的声明期赋值类型（含 var (a,b) / (dynamic a, int b) 解构声明、
    /// foreach 解构；out var 调用点不可追溯 → null）。
    /// </summary>
    private static ITypeSymbol? DesignationDeclarationType(
        Compilation compilation,
        SemanticModel model,
        SingleVariableDesignationSyntax designation)
    {
        // 分量在 var (..) 括号内的位置路径（外→内）。
        var path = new List<int>();
        SyntaxNode? current = designation;
        while (current.Parent is ParenthesizedVariableDesignationSyntax parent)
        {
            path.Add(IndexOf(parent.Variables, (VariableDesignationSyntax)current!));
            current = parent;
        }

        path.Reverse();

        if (current.Parent is not DeclarationExpressionSyntax declaration)
        {
            // 模式变量等解构形态：M2 不做分支收窄（§4.4 P2 说明），保守不可解析。
            return null;
        }

        var anchor = (SyntaxNode)declaration;

        // 显式类型解构分量 (dynamic a, int b)：声明表达式包在 TupleExpression 参数里，
        // 把分量在外层元组（含嵌套元组）中的位置补进路径。
        while (anchor.Parent is ArgumentSyntax argument
               && argument.Parent is TupleExpressionSyntax tuple)
        {
            path.Insert(0, ArgumentIndex(tuple, argument));
            anchor = tuple;
        }

        // foreach（var (x,y) in seq 与 (TA x, TB y) in seq 两形）。
        if (anchor.Parent is ForEachVariableStatementSyntax forEachVariable
            && forEachVariable.Variable.Contains(declaration))
        {
            var element = CollectionElementType(
                compilation, ExpressionType(model, forEachVariable.Expression));
            return DrillTupleType(element, path);
        }

        // 调用实参表中的 out var：由被调方法写入，调用点文本不可追溯 → 保守不可解析。
        if (anchor.Parent is ArgumentSyntax { Parent: ArgumentListSyntax })
        {
            return null;
        }

        // 解构声明赋值：(dynamic a, var b) = rhs / var (a, b) = rhs。
        for (var ancestor = anchor.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (ancestor is AssignmentExpressionSyntax assignment
                && assignment.Left.Contains(declaration))
            {
                return DrillTupleType(ExpressionType(model, assignment.Right), path);
            }
        }

        return null;
    }

    private static int ArgumentIndex(TupleExpressionSyntax tuple, ArgumentSyntax argument)
    {
        for (var i = 0; i < tuple.Arguments.Count; i++)
        {
            if (ReferenceEquals(tuple.Arguments[i], argument))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>按分量位置路径钻取元组类型（ValueTuple：TupleElements 优先，泛型实参兜底）。</summary>
    private static ITypeSymbol? DrillTupleType(ITypeSymbol? tupleType, IReadOnlyList<int> path)
    {
        var current = tupleType;
        foreach (var index in path)
        {
            var elements = (current as INamedTypeSymbol)?.TupleElements;
            if (elements is { Length: > 0 } && index < elements.Value.Length)
            {
                current = elements.Value[index].Type;
                continue;
            }

            if (current is INamedTypeSymbol named
                && index >= 0 && index < named.TypeArguments.Length)
            {
                current = named.TypeArguments[index];
                continue;
            }

            return null;
        }

        return current;
    }

    /// <summary>
    /// 集合表达式的 foreach 元素类型：数组→元素；string→char；IEnumerable&lt;T&gt;
    /// （含 List&lt;T&gt; 等实现，取唯一实现，多个不同实现保守不可解析）→T；
    /// 非泛型 IEnumerable→object；dynamic 集合→dynamic；鸭子类型枚举器不可解析。
    /// </summary>
    private static ITypeSymbol? CollectionElementType(Compilation compilation, ITypeSymbol? collectionType)
    {
        switch (collectionType)
        {
            case null:
                return null;
            case { TypeKind: TypeKind.Dynamic or TypeKind.Error }:
                return collectionType;
            case IArrayTypeSymbol array:
                return array.ElementType;
            case { SpecialType: SpecialType.System_String }:
                return compilation.GetSpecialType(SpecialType.System_Char);
            case INamedTypeSymbol named:
                var genericEnumerable = compilation.GetSpecialType(
                    SpecialType.System_Collections_Generic_IEnumerable_T);

                ITypeSymbol? match = null;
                if (named.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T)
                {
                    match = named.TypeArguments[0];
                }

                foreach (var @interface in named.AllInterfaces)
                {
                    if (!SymbolEqualityComparer.Default.Equals(
                            @interface.OriginalDefinition, genericEnumerable.OriginalDefinition))
                    {
                        continue;
                    }

                    var argument = @interface.TypeArguments[0];
                    if (match != null && !SymbolEqualityComparer.IncludeNullability.Equals(match, argument))
                    {
                        return null;
                    }

                    match = argument;
                }

                if (match != null)
                {
                    return match;
                }

                if (named.SpecialType == SpecialType.System_Collections_IEnumerable
                    || named.AllInterfaces.Any(i =>
                        i.SpecialType == SpecialType.System_Collections_IEnumerable))
                {
                    return compilation.GetSpecialType(SpecialType.System_Object);
                }

                return null;
            default:
                return null;
        }
    }

    private static ITypeSymbol? ExpressionType(SemanticModel model, ExpressionSyntax expression)
    {
        var info = model.GetTypeInfo(expression);
        return info.Type ?? info.ConvertedType;
    }

    /// <summary>
    /// 赋值点流入类型：先取自然类型 <c>Type ?? ConvertedType</c>；自然类型为 dynamic
    /// 且右值是方法调用时，尝试 <see cref="TryResolveSourceDynamicReturn"/> 的 M2
    /// 窄口径透视。透视失败保持 dynamic（后续并集判 DynamicAssignment，宁假阳）。
    /// </summary>
    private static ITypeSymbol? ResolveFlowingType(
        Compilation compilation,
        SemanticModel model,
        ExpressionSyntax expression)
    {
        var info = model.GetTypeInfo(expression);
        var type = info.Type ?? info.ConvertedType;
        if (type?.TypeKind == TypeKind.Dynamic
            && expression is InvocationExpressionSyntax invocation)
        {
            return TryResolveSourceDynamicReturn(compilation, invocation) ?? type;
        }

        return type;
    }

    /// <summary>
    /// M2 窄口径源内透视（A1 正例 <c>var x = DynamicReturning();</c> 所需，最小调整）：
    /// 对 dynamic 返回的源内方法/本地函数调用，当且仅当目标满足全部保守条件时，把其
    /// 全部 return 表达式的唯一静态类型 T 视为调用点流入类型：
    /// ①定义在本编译源码内（外部程序集/元数据不可追溯 → 放弃）；
    /// ②非 virtual/abstract/override（静态方法/本地函数无虚分派面；实例方法仅当宿主
    ///   sealed 或值类型，杜绝派生覆写体不受控）；
    /// ③单一语法定义（partial 多定义放弃）；
    /// ④方法体内所有 return（含表达式体；不含嵌套本地函数/lambda 内 return）类型为
    ///   同一非 dynamic/非 error 封闭 T；yield/裸 return/多类型/零 return 一律放弃。
    /// 这不是通用跨过程分析（D11 不变）：不追踪参数流、不跨程序集、不做调用图收窄；
    /// 拿不准一律回退普通 dynamic 判定。
    /// </summary>
    private static ITypeSymbol? TryResolveSourceDynamicReturn(
        Compilation compilation,
        InvocationExpressionSyntax invocation)
    {
        var symbol = compilation.GetSemanticModel(invocation.SyntaxTree)
            .GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (symbol is not { ReturnType.TypeKind: TypeKind.Dynamic })
        {
            return null;
        }

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride
            || symbol.MethodKind is not (MethodKind.Ordinary or MethodKind.LocalFunction))
        {
            return null;
        }

        if (!symbol.IsStatic && symbol.MethodKind == MethodKind.Ordinary)
        {
            var container = symbol.ContainingType;
            if (container == null || !container.IsSealed && !container.IsValueType)
            {
                return null;
            }
        }

        var references = symbol.DeclaringSyntaxReferences;
        if (references.Length != 1)
        {
            return null;
        }

        var reference = references[0];
        if (!compilation.SyntaxTrees.Contains(reference.SyntaxTree))
        {
            return null;
        }

        SyntaxNode? scope = reference.GetSyntax();
        SyntaxNode? body = scope switch
        {
            BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
            LocalFunctionStatementSyntax localFunction =>
                (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody?.Expression,
            _ => null,
        };
        if (scope == null || body == null)
        {
            return null;
        }

        var returnModel = compilation.GetSemanticModel(reference.SyntaxTree);
        var seen = new HashSet<ITypeSymbol>(SymbolEqualityComparer.IncludeNullability);

        if (scope is BaseMethodDeclarationSyntax { ExpressionBody: { } } or LocalFunctionStatementSyntax { ExpressionBody: { } })
        {
            var arrow = scope switch
            {
                BaseMethodDeclarationSyntax method => method.ExpressionBody!.Expression,
                LocalFunctionStatementSyntax localFunction => localFunction.ExpressionBody!.Expression,
                _ => null,
            };
            if (arrow == null)
            {
                return null;
            }

            return SingleTypeOrNull(returnModel, arrow, seen);
        }

        // 只取本方法体直属 return；不穿入嵌套匿名函数/本地函数（其 return 不属本方法）。
        foreach (var node in body.DescendantNodes(
                     child => child is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        {
            if (node is not ReturnStatementSyntax { Expression: { } returnValue })
            {
                continue;
            }

            if (SingleTypeOrNull(returnModel, returnValue, seen) == null)
            {
                return null;
            }
        }

        return seen.Count == 1 ? seen.Single() : null;
    }

    private static ITypeSymbol? SingleTypeOrNull(
        SemanticModel model,
        ExpressionSyntax expression,
        HashSet<ITypeSymbol> seen)
    {
        var info = model.GetTypeInfo(expression);
        var type = info.Type ?? info.ConvertedType;
        if (type == null || type.TypeKind is TypeKind.Dynamic or TypeKind.Error)
        {
            return null;
        }

        seen.Add(type);
        return seen.Count == 1 ? type : null;
    }

    // ---------- ② 逃逸分析 ----------

    /// <summary>变量作为 ref/out/in 实参（Argument.RefKindKeyword 非 None）→ ByRefEscape。</summary>
    private static bool HasByRefEscape(SyntaxNode body, SemanticModel model, ILocalSymbol local)
    {
        foreach (var argument in body.DescendantNodes().OfType<ArgumentSyntax>())
        {
            if (argument.RefKindKeyword.Kind() == SyntaxKind.None)
            {
                continue;
            }

            if (argument.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => IsReferenceTo(model, id, local)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 捕获与特殊位置（M2 一律保守，不做闭包存活分析）：
    /// 被方法体内任何匿名函数/本地函数子树引用；出现在 fixed/lock/using（含 await using）
    /// /await 动态等待位置。
    /// </summary>
    private static bool IsCapturedOrSpecialPosition(
        SyntaxNode body,
        SyntaxNode candidate,
        SemanticModel model,
        ILocalSymbol local)
    {
        bool ReferencesLocal(SyntaxNode node)
            => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(id => IsReferenceTo(model, id, local));

        // 匿名函数/lambda 捕获（含嵌套；引用落在其任意子树内即拒）。
        foreach (var function in body.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            if (ReferencesLocal(function))
            {
                return true;
            }
        }

        // 本地函数捕获。
        foreach (var localFunction in body.DescendantNodes().OfType<LocalFunctionStatementSyntax>())
        {
            if (ReferencesLocal(localFunction))
            {
                return true;
            }
        }

        // lock(d)：锁对象位置。
        foreach (var @lock in body.DescendantNodes().OfType<LockStatementSyntax>())
        {
            if (ReferencesLocal(@lock.Expression))
            {
                return true;
            }
        }

        // fixed 块：fixed 指针子树内引用即拒（保守：不区分指针初始化器与块内普通引用）。
        foreach (var @fixed in body.DescendantNodes().OfType<FixedStatementSyntax>())
        {
            if (@fixed.Contains(candidate) || ReferencesLocal(@fixed))
            {
                return true;
            }
        }

        // using：传统 using(...) 资源声明/资源表达式，或 using/await using 声明局部。
        foreach (var @using in body.DescendantNodes().OfType<UsingStatementSyntax>())
        {
            if (@using.Contains(candidate)
                || (@using.Expression != null && ReferencesLocal(@using.Expression)))
            {
                return true;
            }
        }

        if (candidate is VariableDeclaratorSyntax
            && candidate.Ancestors().OfType<LocalDeclarationStatementSyntax>()
                .Any(statement => statement.UsingKeyword.Kind() != SyntaxKind.None))
        {
            return true;
        }

        // await d：动态等待位置。
        foreach (var awaitExpression in body.DescendantNodes().OfType<AwaitExpressionSyntax>())
        {
            if (ReferencesLocal(awaitExpression.Expression))
            {
                return true;
            }
        }

        return false;
    }

    // ---------- ③ sealed 校验 ----------

    /// <summary>
    /// T 为值类型（struct/enum/基本类型/Nullable&lt;T&gt;/具体元组）、string、sealed 类/
    /// 委托、sealed 封闭泛型实例 → 通过。开放类型（非 sealed 类/接口/类型参数/含开放
    /// 类型参数的泛型）→ SealedNotSatisfied。sealed 限定消去成员隐藏这一唯一系统性
    /// 语义差（D2）；M2 不做逐成员虚/隐藏判定。
    /// </summary>
    private static bool IsSealedConcrete(ITypeSymbol type)
    {
        if (ContainsOpenTypeParameter(type))
        {
            return false;
        }

        if (type.IsValueType && type.TypeKind != TypeKind.TypeParameter)
        {
            return true;
        }

        if (type.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        // 数组 TypeKind.Array 不在通过面（不把数组隐式当 sealed 引用类型）；
        // 接口/类型参数/dynamic/error 同样在此被拒。
        return type.TypeKind is TypeKind.Class or TypeKind.Delegate
               && type.IsSealed;
    }

    /// <summary>类型（含数组元素、泛型实参）是否含开放类型参数。</summary>
    private static bool ContainsOpenTypeParameter(ITypeSymbol? type)
    {
        switch (type)
        {
            case null:
                return false;
            case { TypeKind: TypeKind.TypeParameter }:
                return true;
            case IArrayTypeSymbol array:
                return ContainsOpenTypeParameter(array.ElementType);
            case IPointerTypeSymbol pointer:
                return ContainsOpenTypeParameter(pointer.PointedAtType);
            case INamedTypeSymbol named:
                foreach (var argument in named.TypeArguments)
                {
                    if (ContainsOpenTypeParameter(argument))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    // ---------- 工具 ----------

    private static SyntaxNode? FindOwner(SyntaxNode node)
    {
        for (var current = node.Parent; current != null; current = current.Parent)
        {
            if (current is BaseMethodDeclarationSyntax
                or AccessorDeclarationSyntax
                or LocalFunctionStatementSyntax
                or AnonymousFunctionExpressionSyntax)
            {
                return current;
            }
        }

        return null;
    }

    private static SyntaxNode? OwnerBody(SyntaxNode owner) => owner switch
    {
        BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody?.Expression,
        AccessorDeclarationSyntax accessor =>
            (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody?.Expression,
        LocalFunctionStatementSyntax localFunction =>
            (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody?.Expression,
        AnonymousFunctionExpressionSyntax anonymous => anonymous.Body,
        _ => null,
    };

    private static bool IsReferenceTo(SemanticModel model, IdentifierNameSyntax identifier, ILocalSymbol local)
        => model.GetSymbolInfo(identifier).Symbol is { } symbol
           && SymbolEqualityComparer.Default.Equals(symbol, local);

    private static int IndexOf<T>(SeparatedSyntaxList<T> list, T item)
        where T : SyntaxNode
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Text(SyntaxNode node)
        => Truncate(WhitespaceRun.Replace(node.ToString(), " ").Trim(), 100);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static FlowLocalClassification Fail(
        DynamicSiteRecord site,
        SyntaxNode node,
        string localName,
        DynamicUnprovableReason reason)
        => new()
        {
            Site = site,
            Declarator = node,
            Provability = DynamicProvability.Unprovable,
            UnprovableReason = reason,
            ProvenType = null,
            ProvenTypeDisplay = null,
            Message = FlowMessages.Unprovable(localName, Text(node), reason),
        };
}

/// <summary>证明器消息（DS001/DS010；§6.1 具名句式，DS010 必带英文原因短语 + 建议动作）。</summary>
internal static class FlowMessages
{
    public static string Proven(string name, string text, string typeDisplay)
        => $"dynamic local '{name}' at '{text}' is provably concretizable to '{typeDisplay}' "
           + $"(dynamic → {typeDisplay}); rewrite mode concretizes the declaration "
           + "(audit mode leaves the source unchanged; no rewrite has been performed yet).";

    public static string Unprovable(string name, string text, DynamicUnprovableReason reason)
        => $"dynamic local '{name}' at '{text}': unprovable ({ReasonPhrase(reason)}); "
           + "change the declaration to a concrete type or register the site in Cs2jDynamicAudit.json.";

    /// <summary>DS001 完成措辞（仅 Rewrite 模式、且声明确已具体化时发射；设计 §6 DS001）。</summary>
    public static string Concretized(string name, string typeDisplay, string location)
        => $"concretized dynamic local declaration to '{typeDisplay}' at {location} "
           + $"(local '{name}'); all use-sites rebound statically on recompilation "
           + $"(dynamic → {typeDisplay}).";

    /// <summary>诊断消息与测试断言共用的英文原因短语（§6 DS010）。</summary>
    public static string ReasonPhrase(DynamicUnprovableReason reason) => reason switch
    {
        DynamicUnprovableReason.SealedNotSatisfied => "sealed not satisfied",
        DynamicUnprovableReason.MultipleAssignmentTypes => "multiple assignment types",
        DynamicUnprovableReason.DynamicAssignment => "dynamic assignment",
        DynamicUnprovableReason.ByRefEscape => "by-ref escape",
        DynamicUnprovableReason.Captured => "captured",
        DynamicUnprovableReason.CrossBoundarySignature => "cross-boundary signature",
        DynamicUnprovableReason.FieldLikeStorage => "field-like storage",
        DynamicUnprovableReason.MetadataOnly => "metadata-only dynamic entry",
        DynamicUnprovableReason.UnresolvedSymbol => "unresolved symbol",
        _ => reason.ToString(),
    };
}
