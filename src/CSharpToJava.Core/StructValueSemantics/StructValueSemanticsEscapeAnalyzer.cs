using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// plan 期「语言无解」构造扫描（设计 §6 SV040/SV041/SV042），三轮：
/// <list type="bullet">
/// <item>硬构造（命中即 plan Infeasible）：SV040-a ref/out this（或可变实例字段
/// 外传）；SV040-b ref return this/可变字段；SV042 指针/ref struct/fixed/取址混叠。</item>
/// <item>this 捕获消解（SV040-c/d 根因修复）：①async/迭代器「实例」方法由 C#
/// 编译器状态机按值捕获 this（写不回外层，本就是语言语义；C04 显式降级同为按值、
/// C18 轻量保留 await），不再判无解；②普通同步实例方法中直接捕获 this 的匿名函数
/// /局部函数（CS1673 形态，含 ref-out-in 生成的 ByRef 访问器闭包）登记为方法级
/// this 持有者改写（copy-in/copy-out 值副本，Phase 3 落地）；仅当方法形态确实
/// 无法保持值语义（async/迭代器内含此形态、表达式树内捕获、无方法体等）才保留
/// SV040 显式失败。</item>
/// <item>this 整体赋值消解（SV041 根因修复）：①普通实例方法（含 async/迭代器/
/// 泛型/partial/表达式体）登记为方法级持有者改写——LHS <c>this</c> 路由为持有者，
/// 出口 copy-out 回写调用者存储，天然覆盖表达式位置/链式/复合/循环/try 全形态；
/// ②构造函数与属性/索引器/事件访问器不能走持有者（构造函数 this 起始未赋值，
/// copy-in 触发 CS0185/CS0165），语句形态 <c>this = expr</c> 登记为字段式展开
/// （承接 E13 ConstructorNormalizer 的既定模型：<c>var t = expr; this.f1 = t.f1;
/// …</c>，构造函数内 readonly 字段/get-only 自动属性后备亦可写）；③含 base 访问
/// 的方法不能改道持有者（base. 非虚调用必须落在真实接收者），无 readonly 实例字段
/// 时同样走字段式展开。仅真正无保持形态（访问器含 readonly 字段、构造函数/访问器
/// 内非语句位置/复合 this 赋值、base 方法遇非语句位置或 readonly 字段、静态/未知
/// 成员）才保留 SV041 显式失败。</item>
/// </list>
/// 方向性取舍（设计 §10-3）：真·无法保持值语义宁可显式报错；假阳由精确接收者
/// 分析消解（显式其它接收者 <c>builder.AsSpan()</c> 不构成 this 捕获）。
/// 只扫该类型各声明部件的直接成员体，嵌套类型成员不属本 struct。
/// </summary>
internal static class StructValueSemanticsEscapeAnalyzer
{
    /// <summary>命中无解硬构造时把 plan 置为 Infeasible；否则登记 this 捕获改写。返回是否命中硬构造。</summary>
    public static bool Detect(ClonePlan plan, Compilation compilation)
    {
        var symbol = plan.Symbol;
        var parts = symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax().FirstAncestorOrSelf<TypeDeclarationSyntax>())
            .Where(p => p != null)
            .Distinct()
            .Cast<TypeDeclarationSyntax>()
            .ToList();
        if (parts.Count == 0) return false;

        // SV042-a：指针类型 / ref struct 类型实例字段（符号面，一次判定）。
        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic) continue;
            if (field.Type is IPointerTypeSymbol)
            {
                Mark(plan, StructValueSemanticsDiagnosticCode.SV042,
                    "struct declares a pointer-typed instance field; pointer aliasing crosses " +
                    "the defensive clone boundary and cannot be rewritten safely.",
                    field.Locations.FirstOrDefault(l => l.IsInSource));
                return true;
            }
            if (field.Type.IsRefLikeType)
            {
                Mark(plan, StructValueSemanticsDiagnosticCode.SV042,
                    "struct declares a ref-struct-typed instance field; ref struct aliasing (C01/C06 " +
                    "territory) crosses the defensive clone boundary.",
                    field.Locations.FirstOrDefault(l => l.IsInSource));
                return true;
            }
        }

        // 第一轮：硬构造（SV040-a/b、SV042），任一命中立即 Infeasible。
        // SV041（this 整体赋值）改由第三轮语义改写消解，此处不再判死。
        foreach (var part in parts)
        {
            var model = compilation.GetSemanticModel(part.SyntaxTree);
            if (TryDetectHardConstruct(part, symbol, model,
                    out var code, out var reason, out var location))
            {
                Mark(plan, code, reason, location);
                return true;
            }
        }

        // 第二轮：可消解的 this 捕获登记为方法级持有者改写；遇无法保持的形态仍 SV040。
        foreach (var part in parts)
        {
            var model = compilation.GetSemanticModel(part.SyntaxTree);
            if (TryPlanThisCaptures(part, symbol, model, plan,
                    out var blockedCode, out var blockedReason, out var blockedLocation))
            {
                Mark(plan, blockedCode, blockedReason, blockedLocation);
                return true;
            }
        }

        // 第三轮：this 整体赋值登记持有者改写（方法）或字段式展开（ctor/访问器/base 方法）；
        // 无保持形态的成员/位置仍显式 SV041。
        foreach (var part in parts)
        {
            var model = compilation.GetSemanticModel(part.SyntaxTree);
            if (TryPlanThisAssignments(part, symbol, model, plan,
                    out var blockedCode, out var blockedReason, out var blockedLocation))
            {
                Mark(plan, blockedCode, blockedReason, blockedLocation);
                return true;
            }
        }

        return false;
    }

    private static void Mark(
        ClonePlan plan,
        StructValueSemanticsDiagnosticCode code,
        string reason,
        Location? location)
    {
        plan.Outcome = ClonePlanOutcome.Infeasible;
        plan.ShimKind = ShimKind.Infeasible;
        plan.FailCode = code;
        plan.FailReason = reason;
        plan.FailLocation = location;
    }

    private static bool TryDetectHardConstruct(
        TypeDeclarationSyntax part,
        INamedTypeSymbol symbol,
        SemanticModel model,
        out StructValueSemanticsDiagnosticCode code,
        out string reason,
        out Location? location)
    {
        code = default;
        reason = string.Empty;
        location = null;

        // 仅在该类型「直接成员」体内扫描（嵌套类型有自己的 this/plan）。
        bool IsInDirectMember(SyntaxNode node) =>
            StructValueSemanticsEscapeAnalyzer.IsInDirectMember(node, part);

        foreach (var node in part.DescendantNodes())
        {
            if (!IsInDirectMember(node)) continue;

            // SV040-a：M(ref this) / M(ref _mutableField) / M(out this)
            if (node is ArgumentSyntax { RefKindKeyword: var refKind } argument
                && (refKind.IsKind(SyntaxKind.RefKeyword) || refKind.IsKind(SyntaxKind.OutKeyword))
                && RootsAtThisOrMutableField(argument.Expression, symbol, model))
            {
                code = StructValueSemanticsDiagnosticCode.SV040;
                reason = "struct passes 'this' or a mutable instance field by ref/out; the callee can " +
                    "alias storage that a defensive Clone() cannot protect.";
                location = argument.GetLocation();
                return true;
            }

            // SV040-b：ref return this / ref return 可变实例字段
            if (IsRefReturn(node, model, out var returned)
                && RootsAtThisOrMutableField(returned, symbol, model, bareThisOnly: false))
            {
                code = StructValueSemanticsDiagnosticCode.SV040;
                reason = "struct member returns 'this' or a mutable instance field by reference; " +
                    "callers can alias storage beyond any defensive Clone().";
                location = node.GetLocation();
                return true;
            }

            // SV042-b/c：fixed 固定 / stackalloc / 对 this 或本类型值取址
            if (node is FixedStatementSyntax or StackAllocArrayCreationExpressionSyntax)
            {
                code = StructValueSemanticsDiagnosticCode.SV042;
                reason = "struct method uses a fixed/stackalloc/pointer pin that aliases storage " +
                    "across the clone boundary.";
                location = node.GetLocation();
                return true;
            }

            // SV042-d：ref struct × 普通 struct 混叠（Span<Self> 形参/局部承载本类型存储）。
            if (node is ParameterSyntax or VariableDeclaratorSyntax
                && IsInDirectMember(node)
                && GetNodeType(node, model) is { IsRefLikeType: true } refLikeType
                && TypeReferencesSelf(refLikeType, symbol))
            {
                code = StructValueSemanticsDiagnosticCode.SV042;
                reason = "struct member exposes its storage through a ref struct (e.g. Span<S> parameter " +
                    "or local); the ref-alias boundary is handled by C01/C06 and cannot be cloned.";
                location = node.GetLocation();
                return true;
            }

            if (node is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.AddressOfExpression } addressOf)
            {
                var operand = addressOf.Operand;
                var rootsAtInstanceState = RootsAtThisOrMutableField(operand, symbol, model);
                var operandType = model.GetTypeInfo(operand).Type;
                var isLocalOfSelf = !rootsAtInstanceState
                    && operandType != null
                    && SymbolEqualityComparer.Default.Equals(
                        operandType.OriginalDefinition, symbol.OriginalDefinition)
                    && UnwrapParens(operand) is IdentifierNameSyntax;
                if (rootsAtInstanceState || isLocalOfSelf)
                {
                    code = StructValueSemanticsDiagnosticCode.SV042;
                    reason = "struct method takes the address of 'this', an instance field, or a local " +
                        "of the struct type; pointer aliasing cannot be expressed by the clone model.";
                    location = node.GetLocation();
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 扫描本部件直接成员中的 this 捕获匿名函数/局部函数：可消解形态（普通同步实例
    /// 方法，含表达式体）登记到 <paramref name="plan"/>；遇无法保持值语义的成员形态
    /// （非方法成员、static 上下文、表达式树捕获、base 访问、async/迭代器方法）
    /// 返回 blocked=SV040 显式失败，绝不静默放过。
    /// </summary>
    private static bool TryPlanThisCaptures(
        TypeDeclarationSyntax part,
        INamedTypeSymbol symbol,
        SemanticModel model,
        ClonePlan plan,
        out StructValueSemanticsDiagnosticCode blockedCode,
        out string blockedReason,
        out Location? blockedLocation)
    {
        blockedCode = default;
        blockedReason = string.Empty;
        blockedLocation = null;

        // 方法声明 → 该方法内首个捕获作用域（语法序）。
        var firstCaptureByMethod =
            new Dictionary<MethodDeclarationSyntax, SyntaxNode>(ReferenceEqualityComparer.Instance);

        foreach (var node in part.DescendantNodes())
        {
            if (node is not (LambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax
                or LocalFunctionStatementSyntax))
            {
                continue;
            }
            // 嵌套类型成员有自己的 this/plan。
            if (node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() != part) continue;
            if (ThisCaptureFacts.Classify(node, symbol, model) == ThisCaptureKind.None) continue;

            // 直接成员：属性/索引器访问器的首个成员祖先即 AccessorDeclarationSyntax。
            var member = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault();
            if (member is not MethodDeclarationSyntax method
                || method.Parent is not TypeDeclarationSyntax declaringType
                || declaringType != part)
            {
                return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                    "an anonymous function or local function in a struct non-method member (constructor, " +
                    "property/indexer/event accessor, operator) captures 'this'; only ordinary instance " +
                    "methods support the this-holder copy-in/copy-out rewrite.",
                    node.GetLocation());
            }

            if (method.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                // 合法代码中 static 上下文不可能引用实例态；保持显式失败，不静默跳过。
                return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                    "an anonymous function or local function inside a static struct member references " +
                    "instance state; no this holder can be introduced in a static context.",
                    node.GetLocation());
            }

            firstCaptureByMethod.TryAdd(method, node);
        }

        foreach (var (method, firstCapture) in firstCaptureByMethod)
        {
            var scope = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (scope == null) continue; // 无体 partial 签名：实现部件负责登记。

            // 表达式树内的 lambda 引用 this：改写会改变表达式树节点形态，无法保持。
            foreach (var lambda in method.DescendantNodes().OfType<LambdaExpressionSyntax>())
            {
                if (ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda)
                    && ThisCaptureFacts.Classify(lambda, symbol, model) != ThisCaptureKind.None)
                {
                    return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                        "an expression-tree lambda inside a struct instance method captures 'this'; " +
                        "the quoted expression cannot be rewritten to a this holder.",
                        lambda.GetLocation());
                }
            }

            // base.X() 必须保持非虚 base 调用语义，无法经持有者接收者表达。
            if (scope.DescendantNodes().OfType<BaseExpressionSyntax>().Any())
            {
                return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                    "a struct instance method both captures 'this' in an anonymous function/local " +
                    "function and uses a 'base.' member access; the non-virtual base call cannot be " +
                    "routed through the this holder.",
                    firstCapture.GetLocation());
            }

            // async/迭代器「方法本体」的状态机生命周期跨越 await/yield：同步
            // copy-in/copy-out 无法表达（嵌套局部函数的 async/yield 不在此列——
            // 它们与普通 lambda 一样只捕获持有者局部）。
            if (method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                || method.DescendantNodes().OfType<YieldStatementSyntax>()
                    .Any(y => IsDirectlyInMember(y, method)))
            {
                return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                    "an anonymous function or local function inside an async/iterator struct instance " +
                    "method captures 'this'; state-machine capture timing cannot preserve value " +
                    "semantics through the this-holder rewrite.",
                    firstCapture.GetLocation());
            }

            if (model.GetDeclaredSymbol(method) is not IMethodSymbol methodSymbol)
            {
                return BlockThisCapture(out blockedCode, out blockedReason, out blockedLocation,
                    "struct method with a 'this'-capturing anonymous function could not be bound to a " +
                    "method symbol for the this-holder rewrite.",
                    firstCapture.GetLocation());
            }

            if (plan.ThisCaptureRewrites.Any(r =>
                    SymbolEqualityComparer.Default.Equals(r.Method, methodSymbol)))
            {
                continue;
            }

            var methodCapture = ThisCaptureFacts.Classify(
                scope, symbol, model, plan.MutatingMethods);
            var hasWrites = methodCapture == ThisCaptureKind.Writes;

            plan.ThisCaptureRewrites.Add(
                new ThisCaptureRewrite(methodSymbol, hasWrites, firstCapture.GetLocation()));
            plan.TouchedFiles.Add(part.SyntaxTree.FilePath);
        }

        return false;
    }

    /// <summary>
    /// 扫描本部件直接成员中的整体 this 赋值（<c>this = expr</c> / 复合 / this++）：
    /// 普通方法登记方法级持有者改写（与 SV040-d 同机制，LHS this 路由为持有者、
    /// 出口 copy-out）；构造函数/访问器/含 base 访问的方法登记字段式展开；
    /// 成员形态或位置确实无法保持值语义时返回 blocked=SV041 显式失败。
    /// </summary>
    private static bool TryPlanThisAssignments(
        TypeDeclarationSyntax part,
        INamedTypeSymbol symbol,
        SemanticModel model,
        ClonePlan plan,
        out StructValueSemanticsDiagnosticCode blockedCode,
        out string blockedReason,
        out Location? blockedLocation)
    {
        blockedCode = default;
        blockedReason = string.Empty;
        blockedLocation = null;

        var targetsByMember =
            new Dictionary<SyntaxNode, List<SyntaxNode>>(ReferenceEqualityComparer.Instance);

        foreach (var node in part.DescendantNodes())
        {
            if (!IsInDirectMember(node, part)) continue;
            if (!IsWholeThisAssignmentTarget(node, out var assignment)) continue;
            // 幂等：本 pass 生成的出口回写 this = __cs2jTcN 不是源生赋值。
            if (assignment is not null && IsGeneratedCopyOut(assignment.Right)) continue;

            // 直接词法成员：访问器优先（AccessorDeclarationSyntax 在新版 Roslyn 不再
            // 是 MemberDeclarationSyntax 子类；属性/索引器/事件声明本身不是赋值的词法
            // 容器），否则取父节点即本部件类型声明的方法/构造函数。嵌套类型成员已由
            // IsInDirectMember 排除；局部函数是 StatementSyntax，会正确落到外层方法。
            SyntaxNode? member = node.Ancestors()
                .OfType<AccessorDeclarationSyntax>().FirstOrDefault();
            member ??= node.Ancestors()
                .FirstOrDefault(a => a.Parent == part
                    && a is MethodDeclarationSyntax or ConstructorDeclarationSyntax);
            if (member == null)
            {
                return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                    "a whole-struct 'this' assignment appears outside a rewritable struct member " +
                    "(constructor/accessor/instance method).",
                    node.GetLocation());
            }

            if (!targetsByMember.TryGetValue(member, out var list))
            {
                list = new List<SyntaxNode>();
                targetsByMember.Add(member, list);
            }
            list.Add(node);
        }

        foreach (var pair in targetsByMember)
        {
            var member = pair.Key;
            var targets = pair.Value;
            var firstLocation = targets[0].GetLocation();
            var memberSymbol = model.GetDeclaredSymbol(member) as IMethodSymbol;
            if (memberSymbol == null || memberSymbol.IsStatic)
            {
                // static 上下文出现 this 本就是 CS0026；显式失败，不静默改写。
                return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                    "a whole-struct 'this' assignment appears in a static or unbindable member; " +
                    "no this holder or field expansion can be introduced.",
                    firstLocation);
            }

            switch (member)
            {
                case MethodDeclarationSyntax method:
                    if (PlanThisAssignmentMethod(method, symbol, targets, memberSymbol,
                            firstLocation, plan))
                    {
                        continue;
                    }
                    return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                        RefineMethodBlockReason(symbol, targets), firstLocation);

                case ConstructorDeclarationSyntax:
                    if (targets.All(IsSimpleStatementPosition))
                    {
                        AddExpansion(plan, memberSymbol, firstLocation, part);
                        continue;
                    }
                    return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                        "a struct constructor assigns to 'this' in a non-statement position (or via a " +
                        "compound assignment/increment); only statement-form 'this = expr' can be " +
                        "expanded field-by-field.",
                        firstLocation);

                case AccessorDeclarationSyntax:
                    if (HasReadOnlyInstanceField(symbol))
                    {
                        return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                            "a struct property/indexer/event accessor assigns to 'this' while the struct " +
                            "declares a readonly instance field (or get-only auto-property); field-by-field " +
                            "expansion cannot assign readonly members outside a constructor.",
                            firstLocation);
                    }
                    if (targets.All(IsSimpleStatementPosition))
                    {
                        AddExpansion(plan, memberSymbol, firstLocation, part);
                        continue;
                    }
                    return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                        "a struct property/indexer/event accessor assigns to 'this' in a non-statement " +
                        "position (or via a compound assignment/increment); only statement-form " +
                        "'this = expr' can be expanded field-by-field.",
                        firstLocation);

                default:
                    return BlockThisAssignment(out blockedCode, out blockedReason, out blockedLocation,
                        "a whole-struct 'this' assignment appears in a member kind that supports neither " +
                        "a this holder nor field-by-field expansion.",
                        firstLocation);
            }
        }

        return false;
    }

    /// <summary>
    /// 方法形态裁决：已登记 this 持有者（含 this 捕获闭包）则把整体赋值的写入并入
    /// 持有者；无 base 访问直接登记持有者；有 base 访问且全部语句形态、无 readonly
    /// 实例字段时走字段式展开。返回 false 表示该方法必须保留 SV041。
    /// </summary>
    private static bool PlanThisAssignmentMethod(
        MethodDeclarationSyntax method,
        INamedTypeSymbol symbol,
        List<SyntaxNode> targets,
        IMethodSymbol methodSymbol,
        Location firstLocation,
        ClonePlan plan)
    {
        var existing = plan.ThisCaptureRewrites
            .FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Method, methodSymbol));
        if (existing != null)
        {
            // 闭包持有者改写会把方法体内全部 this（含整体赋值 LHS）路由到持有者；
            // 整体赋值本身即写入，确保出口 copy-out。
            if (!existing.HasWrites)
            {
                plan.ThisCaptureRewrites.Remove(existing);
                plan.ThisCaptureRewrites.Add(existing with { HasWrites = true });
            }
            return true;
        }

        var scope = (SyntaxNode?)method.Body ?? method.ExpressionBody;
        if (scope == null) return false; // 无体 partial 签名：实现部件负责。

        var hasBase = scope.DescendantNodes()
            .OfType<BaseExpressionSyntax>().Any();

        if (!hasBase)
        {
            plan.ThisCaptureRewrites.Add(
                new ThisCaptureRewrite(methodSymbol, HasWrites: true, firstLocation));
            plan.TouchedFiles.Add(method.SyntaxTree.FilePath);
            return true;
        }

        if (targets.All(IsSimpleStatementPosition) && !HasReadOnlyInstanceField(symbol))
        {
            AddExpansion(plan, methodSymbol, firstLocation,
                method.FirstAncestorOrSelf<TypeDeclarationSyntax>()!);
            return true;
        }

        return false;
    }

    private static string RefineMethodBlockReason(INamedTypeSymbol symbol, List<SyntaxNode> targets)
    {
        if (HasReadOnlyInstanceField(symbol))
        {
            return "a struct instance method both assigns to 'this' and uses a 'base.' member; " +
                "the this-holder rewrite would misroute the non-virtual base call, and field-by-field " +
                "expansion cannot assign the struct's readonly instance field(s) outside a constructor.";
        }
        return "a struct instance method both assigns to 'this' (in a non-statement position or via a " +
            "compound assignment/increment) and uses a 'base.' member; the this-holder rewrite would " +
            "misroute the non-virtual base call, and only statement-form 'this = expr' can be expanded " +
            "field-by-field.";
    }

    private static void AddExpansion(
        ClonePlan plan,
        IMethodSymbol memberSymbol,
        Location location,
        TypeDeclarationSyntax part)
    {
        if (plan.ThisAssignmentExpansions.Any(e =>
                SymbolEqualityComparer.Default.Equals(e.Member, memberSymbol)))
        {
            return;
        }
        plan.ThisAssignmentExpansions.Add(new ThisAssignmentExpansion(memberSymbol, location));
        plan.TouchedFiles.Add(part.SyntaxTree.FilePath);
    }

    private static bool BlockThisAssignment(
        out StructValueSemanticsDiagnosticCode blockedCode,
        out string blockedReason,
        out Location? blockedLocation,
        string reason,
        Location? location)
    {
        blockedCode = StructValueSemanticsDiagnosticCode.SV041;
        blockedReason = reason;
        blockedLocation = location;
        return true;
    }

    /// <summary>
    /// 节点是否为整体 this 赋值目标：<c>this = …</c>/<c>(this) = …</c>（任意赋值运算）
    /// 或 <c>this++</c>/<c>++this</c>；命中简单赋值时回传赋值表达式。
    /// </summary>
    private static bool IsWholeThisAssignmentTarget(
        SyntaxNode node,
        out AssignmentExpressionSyntax? assignment)
    {
        assignment = null;
        if (node is AssignmentExpressionSyntax a
            && UnwrapParens(a.Left) is ThisExpressionSyntax)
        {
            assignment = a;
            return true;
        }
        ExpressionSyntax? unaryOperand = node switch
        {
            PrefixUnaryExpressionSyntax pre when pre.IsKind(SyntaxKind.PreIncrementExpression)
                || pre.IsKind(SyntaxKind.PreDecrementExpression) => pre.Operand,
            PostfixUnaryExpressionSyntax post when post.IsKind(SyntaxKind.PostIncrementExpression)
                || post.IsKind(SyntaxKind.PostDecrementExpression) => post.Operand,
            _ => null,
        };
        if (unaryOperand != null && UnwrapParens(unaryOperand) is ThisExpressionSyntax)
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 整体 this 赋值是否为可字段式展开的语句形态：<c>this = expr;</c>（允许括号）
    /// 或访问器/方法的表达式体 <c>=&gt; this = expr</c>；复合赋值/自增减/表达式位置不展。
    /// </summary>
    private static bool IsSimpleStatementPosition(SyntaxNode node)
    {
        if (node is not AssignmentExpressionSyntax
                { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assignment)
        {
            return false;
        }

        var current = (SyntaxNode)assignment;
        while (current.Parent is ParenthesizedExpressionSyntax)
            current = current.Parent;

        if (current.Parent is ExpressionStatementSyntax) return true;
        if (current.Parent is ArrowExpressionClauseSyntax arrow
            && arrow.Expression == current
            && arrow.Parent is AccessorDeclarationSyntax or MethodDeclarationSyntax)
        {
            return true;
        }
        return false;
    }

    /// <summary>struct 是否声明 readonly 实例字段（含 get-only/init-only 自动属性后备字段）。</summary>
    private static bool HasReadOnlyInstanceField(INamedTypeSymbol symbol)
    {
        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst) continue;
            if (field.IsReadOnly) return true;
        }
        return false;
    }

    private static bool BlockThisCapture(
        out StructValueSemanticsDiagnosticCode blockedCode,
        out string blockedReason,
        out Location? blockedLocation,
        string reason,
        Location? location)
    {
        blockedCode = StructValueSemanticsDiagnosticCode.SV040;
        blockedReason = reason;
        blockedLocation = location;
        return true;
    }

    /// <summary>
    /// yield 是否属于该方法本体（而非嵌套在方法内的局部函数/lambda 中）：沿祖先向上
    /// 先遇到本方法即真，先遇到局部函数/匿名函数即假。
    /// </summary>
    private static bool IsDirectlyInMember(SyntaxNode node, MethodDeclarationSyntax method)
    {
        foreach (var ancestor in node.Ancestors())
        {
            if (ancestor == method) return true;
            if (ancestor is LocalFunctionStatementSyntax
                or LambdaExpressionSyntax
                or AnonymousMethodExpressionSyntax)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>是否本 pass 生成的出口回写 <c>this = __cs2jTcN</c>（幂等保护）。</summary>
    private static bool IsGeneratedCopyOut(ExpressionSyntax expression) =>
        UnwrapParens(expression) is IdentifierNameSyntax identifier
        && identifier.Identifier.ValueText.StartsWith(
            StructValueSemanticsSyntaxFacts.ThisCopyTempPrefix, StringComparison.Ordinal);

    private static bool IsRefReturn(SyntaxNode node, SemanticModel model, out ExpressionSyntax returned)
    {
        returned = null!;
        switch (node)
        {
            // return ref e; 语法上 e 是 RefExpressionSyntax。
            case ReturnStatementSyntax { Expression: RefExpressionSyntax refExpression }:
                returned = refExpression.Expression;
                return true;
            case ArrowExpressionClauseSyntax arrow
                when model.GetDeclaredSymbol(arrow.Parent) is IMethodSymbol { ReturnsByRef: true }
                    or IPropertySymbol { ReturnsByRef: true }:
                returned = arrow.Expression is RefExpressionSyntax arrowRef
                    ? arrowRef.Expression
                    : arrow.Expression;
                return true;
        }
        return false;
    }

    /// <summary>节点是否位于该类型声明的「直接成员」体内（嵌套类型成员不属本 struct）。</summary>
    private static bool IsInDirectMember(SyntaxNode node, TypeDeclarationSyntax part) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == part;

    private static ExpressionSyntax UnwrapParens(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax paren)
            expression = paren.Expression;
        return expression;
    }

    private static ITypeSymbol? GetNodeType(SyntaxNode node, SemanticModel model) =>
        model.GetDeclaredSymbol(node) switch
        {
            IParameterSymbol parameter => parameter.Type,
            ILocalSymbol local => local.Type,
            _ => null,
        };

    /// <summary>ref-like 类型（如 Span&lt;T&gt;）的类型实参是否引用本 struct（含本 struct 的泛型实例）。</summary>
    private static bool TypeReferencesSelf(ITypeSymbol type, INamedTypeSymbol self)
    {
        if (type is INamedTypeSymbol named)
        {
            if (named.IsGenericType)
            {
                foreach (var argument in named.TypeArguments)
                {
                    if (SymbolEqualityComparer.Default.Equals(argument.OriginalDefinition, self.OriginalDefinition))
                        return true;
                }
            }
            return SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, self.OriginalDefinition);
        }
        return false;
    }

    /// <summary>
    /// 表达式根是否为 this（<c>this</c>/<c>this.F</c>）或本类型的隐式/显式可变实例字段
    /// （<c>_f</c>/<c>this._f</c>，readonly 字段不构成可变别名）。
    /// </summary>
    private static bool RootsAtThisOrMutableField(
        ExpressionSyntax expression,
        INamedTypeSymbol symbol,
        SemanticModel model,
        bool bareThisOnly = true)
    {
        var current = UnwrapParens(expression);
        if (current is ThisExpressionSyntax) return true;

        // 剥成员/元素链取根标识符（ref _f / ref this._f / ref _arr[0]）。
        var root = current;
        while (root is MemberAccessExpressionSyntax ma) root = ma.Expression;
        while (root is ElementAccessExpressionSyntax ea) root = ea.Expression;
        root = UnwrapParens(root);

        if (root is not IdentifierNameSyntax identifier) return false;
        // 隐式 this._f：标识符即字段名；this._f 时根为 this 已在上面命中。
        if (identifier.Identifier.Text == "this") return true;

        if (model.GetSymbolInfo(identifier).Symbol is IFieldSymbol field
            && !field.IsStatic
            && !field.IsReadOnly
            && SymbolEqualityComparer.Default.Equals(
                field.ContainingType.OriginalDefinition, symbol.OriginalDefinition))
        {
            return true;
        }

        return false;
    }
}
