using CSharpToJava.Core.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// plan 期「语言无解」构造扫描（设计 §6 SV040/SV041/SV042），两轮：
/// <list type="bullet">
/// <item>硬构造（命中即 plan Infeasible）：SV041 结构体内 <c>this = expr</c>；
/// SV040-a ref/out this（或可变实例字段外传）；SV040-b ref return this/可变字段；
/// SV042 指针/ref struct/fixed/取址混叠。</item>
/// <item>this 捕获消解（SV040-c/d 根因修复）：①async/迭代器「实例」方法由 C#
/// 编译器状态机按值捕获 this（写不回外层，本就是语言语义；C04 显式降级同为按值、
/// C18 轻量保留 await），不再判无解；②普通同步实例方法中直接捕获 this 的匿名函数
/// /局部函数（CS1673 形态，含 ref-out-in 生成的 ByRef 访问器闭包）登记为方法级
/// this 持有者改写（copy-in/copy-out 值副本，Phase 3 落地）；仅当方法形态确实
/// 无法保持值语义（async/迭代器内含此形态、表达式树内捕获、无方法体等）才保留
/// SV040 显式失败。</item>
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

        // 第一轮：硬构造（SV040-a/b、SV041、SV042），任一命中立即 Infeasible。
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
            node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() == part;

        foreach (var node in part.DescendantNodes())
        {
            if (!IsInDirectMember(node)) continue;

            // SV041：this = expr（本 pass 自己生成的出口回写 this = __cs2jTcN 除外：幂等）。
            if (node is AssignmentExpressionSyntax assignment
                && UnwrapParens(assignment.Left) is ThisExpressionSyntax
                && !IsGeneratedCopyOut(assignment.Right))
            {
                code = StructValueSemanticsDiagnosticCode.SV041;
                reason = "struct method assigns to 'this'; the defensive clone model cannot preserve " +
                    "value semantics for whole-struct reassignment (add an explicit Clone() or skip the type).";
                location = assignment.GetLocation();
                return true;
            }

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
