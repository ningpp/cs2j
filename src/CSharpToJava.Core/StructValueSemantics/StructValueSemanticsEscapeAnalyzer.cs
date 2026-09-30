using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.StructValueSemantics;

/// <summary>
/// plan 期「语言无解」构造扫描（设计 §6 SV040/SV041/SV042），命中即 plan Infeasible：
/// <list type="bullet">
/// <item>SV041：结构体内 <c>this = expr</c>（E13 落网形态）。</item>
/// <item>SV040：this 按引用逃逸——ref/out this 或可变实例字段外传；ref 返回 this/可变字段；
/// 逃逸 lambda（捕获实例态且作为实参/赋值/返回离开方法）；触碰实例态的 async/迭代器实例方法。</item>
/// <item>SV042：指针类型实例字段；方法体内 fixed 固定；对 this/实例字段/本类型局部取址。</item>
/// </list>
/// 方向性取舍（设计 §10-3）：宁可假阳（可经 overrides.skip 逃生），不可假阴。
/// 只扫该类型各声明部件的直接成员体，嵌套类型成员不属本 struct。
/// </summary>
internal static class StructValueSemanticsEscapeAnalyzer
{
    /// <summary>命中无解构造时把 plan 置为 Infeasible（含码/原因/位置）；返回是否命中。</summary>
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

        foreach (var part in parts)
        {
            var model = compilation.GetSemanticModel(part.SyntaxTree);
            if (TryDetect(part, symbol, model, out var code, out var reason, out var location))
            {
                Mark(plan, code, reason, location);
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

    private static bool TryDetect(
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

            // SV041：this = expr
            if (node is AssignmentExpressionSyntax assignment
                && UnwrapParens(assignment.Left) is ThisExpressionSyntax)
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

            // SV040-c：触碰实例态的 async/迭代器「实例」方法（this 被状态机捕获）。
            if (node is MethodDeclarationSyntax method
                && !method.Modifiers.Any(SyntaxKind.StaticKeyword)
                && (method.Modifiers.Any(SyntaxKind.AsyncKeyword)
                    || method.DescendantNodes().OfType<YieldStatementSyntax>().Any())
                && ReferencesInstanceState(method, symbol, model))
            {
                code = StructValueSemanticsDiagnosticCode.SV040;
                reason = "async/iterator instance method captures 'this' in a state machine; mutations " +
                    "through the captured reference cannot preserve struct value semantics.";
                location = method.Identifier.GetLocation();
                return true;
            }

            // SV040-d：捕获实例态的 lambda/匿名方法逃逸（实参/赋值初始化/返回）。
            if ((node is LambdaExpressionSyntax || node is AnonymousMethodExpressionSyntax)
                && ReferencesInstanceState(node, symbol, model)
                && LambdaEscapes(node))
            {
                code = StructValueSemanticsDiagnosticCode.SV040;
                reason = "lambda capturing instance state escapes the method (passed as argument, " +
                    "assigned, or returned); the captured 'this' aliases shared storage.";
                location = node.GetLocation();
                return true;
            }
        }

        return false;
    }

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

    private static bool LambdaEscapes(SyntaxNode lambda)
    {
        // 实参逃逸（M(() => X)）
        if (lambda.Parent is ArgumentSyntax) return true;
        // 赋值/字段或局部初始化逃逸
        if (lambda.Parent is EqualsValueClauseSyntax) return true;
        // 返回逃逸
        if (lambda.Ancestors().OfType<ReturnStatementSyntax>().FirstOrDefault() is { } ret
            && !ret.Ancestors().OfType<LambdaExpressionSyntax>().Any())
        {
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

    /// <summary>节点（方法体/lambda 体）内是否引用 this 或本类型的实例成员（捕获 this 的条件）。</summary>
    private static bool ReferencesInstanceState(
        SyntaxNode scope,
        INamedTypeSymbol symbol,
        SemanticModel model)
    {
        if (scope.DescendantNodesAndSelf().Any(n => n is ThisExpressionSyntax)) return true;

        foreach (var identifier in scope.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var member = model.GetSymbolInfo(identifier).Symbol;
            if (member is { IsStatic: false }
                && SymbolEqualityComparer.Default.Equals(
                    member.ContainingType?.OriginalDefinition, symbol.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }
}
