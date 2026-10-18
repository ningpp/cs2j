using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CSharpToJava.Core.Analysis;

namespace CSharpToJava.Core.EqualityConcat;

/// <summary>
/// Semantic categories emitted by <see cref="SemanticKindClassifier"/>
/// (design §4.1). Every rule shares this single classifier; rule files never
/// guess categories from syntax shapes.
/// </summary>
internal enum EqualityConcatKind
{
    /// <summary>string ==/!= bound to string.op_Equality/op_Inequality (EC-1).</summary>
    StringEquality,

    /// <summary>Reference identity bound to object.op_Equality, incl. (object) escapes (EC-2).</summary>
    ObjectIdentity,

    /// <summary>delegate ==/!= bound to the delegate type's predefined operator (EC-6).</summary>
    DelegateEquality,

    /// <summary>record class/struct synthesized == (UserDefinedOperator, implicit, record) (EC-7).</summary>
    RecordValueEquality,

    /// <summary>Pure string + bound to string.op_Addition (EC-3).</summary>
    StringConcat,

    /// <summary>string += compound assignment bound to string.op_Addition (EC-4).</summary>
    StringConcatCompound,

    /// <summary>Lifted ==/!= (Nullable), assigned to the C22 pass (EC-8).</summary>
    LiftedNullableEquality,

    /// <summary>Tuple-typed binary that E06 should already have rewritten (EC-9 edge).</summary>
    TupleEqualityResidual,

    /// <summary>event == null: left alone, the converter has a dedicated path.</summary>
    EventNullCheck,

    /// <summary>Source/metadata user-defined operator: belongs to C14.</summary>
    UserDefinedOperator,

    /// <summary>Predefined primitive/enum/other operator: intentionally untouched.</summary>
    PredefinedOther,

    /// <summary>Site inside an Expression&lt;Lambda&gt; tree (skip, CS2J0472).</summary>
    ExpressionTree,

    /// <summary>Dynamically bound site (CS2J0471).</summary>
    Dynamic,

    /// <summary>Neither operation nor symbols can classify the site (CS2J0470).</summary>
    Unknown,
}

/// <summary>Result of classifying one binary/assignment syntax node.</summary>
internal sealed class EqualityConcatClassification
{
    public required EqualityConcatKind Kind;

    /// <summary>Bound operator method (symbol-primary judgment, design §2 decision 1).</summary>
    public IMethodSymbol? BoundMethod;

    /// <summary>True for an equality site (<c>==</c>/<c>!=</c>).</summary>
    public bool IsEquality;

    /// <summary>True when the equality site is <c>!=</c> (or a negated kind).</summary>
    public bool IsNotEquals;

    /// <summary>Operation returned for the node, when present.</summary>
    public IOperation? Operation;
}

/// <summary>
/// The single semantic classifier of the equality/concat pass. Per the M0
/// probe (Roslyn referenced by this repository; probe project under
/// tests/EqualityConcatProbe), category distinction is driven primarily by
/// the bound symbol from GetSymbolInfo/OperatorMethod and secondarily by the
/// public <see cref="BinaryOperatorKind"/>: all equality sites report the
/// public kind Equals/NotEquals and string concatenation reports Add, so the
/// internal CSharp BinaryOperatorKind names (StringEqual etc.) must never be
/// used for public API judgment.
/// </summary>
internal static class SemanticKindClassifier
{
    /// <summary>Classify a <see cref="BinaryExpressionSyntax"/> or <see cref="AssignmentExpressionSyntax"/>.</summary>
    public static EqualityConcatClassification Classify(SyntaxNode node, SemanticModel model)
    {
        // Pipeline step 1: expression-tree ancestor short-circuit.
        var expressionTreeLambda = node.Ancestors().OfType<LambdaExpressionSyntax>()
            .FirstOrDefault(lambda => ExpressionTreeFacts.IsExpressionTreeLambda(model, lambda));
        if (expressionTreeLambda != null)
        {
            return New(EqualityConcatKind.ExpressionTree);
        }

        var typeInfo = model.GetTypeInfo(node);

        // Pipeline step 3: dynamic binding.
        if (IsDynamic(typeInfo.Type) || IsDynamic(typeInfo.ConvertedType))
        {
            return New(EqualityConcatKind.Dynamic);
        }

        var operation = model.GetOperation(node);

        switch (operation)
        {
            case ICompoundAssignmentOperation compound:
                return ClassifyCompound(node, model, compound, typeInfo);

            case ITupleBinaryOperation:
                return New(EqualityConcatKind.TupleEqualityResidual, operation: operation);

            case IBinaryOperation binary:
                return ClassifyBinary(node, model, binary);

            case IDynamicInvocationOperation:
                return New(EqualityConcatKind.Dynamic, operation: operation);

            case ICoalesceOperation:
                // ?? 不是相等/拼接站点（候选词过滤已排除，直接调用分类器时
                // 同样归为预定义形态，透传不改写）。
                return New(EqualityConcatKind.PredefinedOther, operation: operation);

            case IInvalidOperation:
                // IInvalidOperation always carries compiler diagnostics (e.g.
                // CS0019 for tuple == null); rely on them and do not invent a
                // site (design §2 decision 15).
                return New(EqualityConcatKind.PredefinedOther, operation: operation);

            default:
                return ClassifyFallback(node, model, typeInfo, operation);
        }
    }

    private static EqualityConcatClassification ClassifyBinary(
        SyntaxNode node,
        SemanticModel model,
        IBinaryOperation binary)
    {
        var isEquality = binary.OperatorKind is BinaryOperatorKind.Equals
            or BinaryOperatorKind.NotEquals
            or BinaryOperatorKind.ObjectValueEquals
            or BinaryOperatorKind.ObjectValueNotEquals;
        var isNotEquals = binary.OperatorKind is BinaryOperatorKind.NotEquals
            or BinaryOperatorKind.ObjectValueNotEquals;

        // Pipeline step 3: lifted equality -> C22 (everything lifted ==/!=,
        // including lifted user-defined operators).
        if (binary.IsLifted && isEquality)
        {
            return New(EqualityConcatKind.LiftedNullableEquality,
                BoundMethodOf(binary.OperatorMethod, node, model),
                isEquality, isNotEquals, binary);
        }

        var method = BoundMethodOf(binary.OperatorMethod, node, model);
        var kind = ClassifyByMethod(
            method, binary.OperatorKind, node, model, isEquality);

        return New(kind, method, isEquality, isNotEquals, binary);
    }

    private static EqualityConcatClassification ClassifyCompound(
        SyntaxNode node,
        SemanticModel model,
        ICompoundAssignmentOperation compound,
        TypeInfo _)
    {
        var method = BoundMethodOf(compound.OperatorMethod, node, model);

        if (compound.IsLifted)
        {
            // Lifted compound arithmetic/concatenation is not a string site.
            return New(EqualityConcatKind.PredefinedOther, method, operation: compound);
        }

        if (compound.OperatorKind == BinaryOperatorKind.Add
            && method is { ContainingType.SpecialType: SpecialType.System_String }
            && method.Name == "op_Addition")
        {
            return New(EqualityConcatKind.StringConcatCompound, method, operation: compound);
        }

        return New(EqualityConcatKind.PredefinedOther, method, operation: compound);
    }

    /// <summary>
    /// Pipeline steps 4-5 symbol dispatch. The bound symbol is the primary
    /// judgment; the public operator kind only separates Add from equality.
    /// </summary>
    private static EqualityConcatKind ClassifyByMethod(
        IMethodSymbol? method,
        BinaryOperatorKind operatorKind,
        SyntaxNode node,
        SemanticModel model,
        bool isEquality)
    {
        if (method == null)
        {
            // 条件逻辑 &&/|| 没有可绑定的 operator method，但属于预定义运算，
            // 不归本 pass（外层短路节点本应在候选词过滤处透传，这里保证直接
            // 调用分类器时也不会误报 Unknown）。
            if (operatorKind is BinaryOperatorKind.ConditionalAnd
                or BinaryOperatorKind.ConditionalOr)
            {
                return EqualityConcatKind.PredefinedOther;
            }

            // 类型兜底：pass 的绑定编译使用 TPA 手工引用集，与真实 SDK 编译并
            // 不严格一致（重载解析可能因引用残缺而失败，连带 IBinaryOperation
            // 的 OperatorMethod 与 GetSymbolInfo 双双为 null）。对 Add 站点,
            // 操作数/结果类型仍足以定性:
            //  - 任一操作数或结果为 string -> 字符串拼接 (C# 的 string + X 恒为
            //    拼接,包括 string + char/值类型装箱);
            //  - 其余 Add (数值/位/委托合并等) 本就不归本 pass,按预定义运算
            //    透传,不应误报 CS2J0470 阻断整链。
            if (operatorKind == BinaryOperatorKind.Add
                && node is BinaryExpressionSyntax addBinary)
            {
                return IsStringConcatByType(addBinary, model)
                    ? EqualityConcatKind.StringConcat
                    : EqualityConcatKind.PredefinedOther;
            }

            // 相等站点 (==/!=) 的类型兜底:与 Add 站点同理,TPA 引用残缺时
            // 符号绑定可能双双为 null。按操作数类型分层兜底:
            //  - 两个 string 操作数且无 (object) 逃逸 -> string equality;
            //  - (object) 逃逸或任一操作数为 object 静态类型 -> object identity;
            //  - delegate 静态类型 (TypeKind.Delegate 或 System.Delegate) ->
            //    delegate equality (event null check 在前面已处理,这里只看
            //    delegate 变量比较);
            //  - 任一操作数为 ErrorType 或 null (类型解析彻底失败) ->
            //    PredefinedOther (透传,不误报 CS2J0470);
            //  - 其余 (int/enum/struct 等值类型,或 null 与非 string 引用类型)
            //    -> PredefinedOther (本就不归本 pass)。
            if (isEquality && node is BinaryExpressionSyntax eqBinary)
                return ClassifyEqualityByTypeFallback(eqBinary, model);

            return EqualityConcatKind.Unknown;
        }

        switch (method.MethodKind)
        {
            case MethodKind.UserDefinedOperator:
                // Record synthesized operators: implicit + record. Covers record
                // class and record struct (M0 probe).
                if (method.IsImplicitlyDeclared && method.ContainingType.IsRecord)
                {
                    return EqualityConcatKind.RecordValueEquality;
                }

                // Source-declared or metadata-only user operators belong to C14.
                return EqualityConcatKind.UserDefinedOperator;

            case MethodKind.BuiltinOperator:
            {
                var containingType = method.ContainingType;

                if (containingType.SpecialType == SpecialType.System_String)
                {
                    if (operatorKind == BinaryOperatorKind.Add && method.Name == "op_Addition")
                    {
                        return EqualityConcatKind.StringConcat;
                    }

                    if (isEquality)
                    {
                        return EqualityConcatKind.StringEquality;
                    }
                }

                if (isEquality && containingType.SpecialType == SpecialType.System_Object)
                {
                    // (ValueTuple)? == null binds object.op_Equality per the M0
                    // probe; ReferenceEquals would change semantics, so treat
                    // it as the tuple residual edge (CS2J0476).
                    if (HasNullableTupleOperand(node, model))
                        return EqualityConcatKind.TupleEqualityResidual;

                    return EqualityConcatKind.ObjectIdentity;
                }

                if (isEquality && IsDelegateContainingType(containingType))
                {
                    // evt == null shares the delegate operator; distinguish by operand.
                    return HasEventOperand(node, model)
                        ? EqualityConcatKind.EventNullCheck
                        : EqualityConcatKind.DelegateEquality;
                }

                return EqualityConcatKind.PredefinedOther;
            }

            default:
                return EqualityConcatKind.PredefinedOther;
        }
    }

    /// <summary>Pipeline step 7: operation-missing fallback (counted and visible).</summary>
    private static EqualityConcatClassification ClassifyFallback(
        SyntaxNode node,
        SemanticModel model,
        TypeInfo typeInfo,
        IOperation? operation)
    {
        if (model.GetSymbolInfo(node).Symbol is IMethodSymbol symbolMethod)
        {
            var isEquality = HasEqualityToken(node);
            var kind = ClassifyByMethod(
                symbolMethod, BinaryOperatorKind.None, node, model, isEquality);
            return New(kind, symbolMethod, isEquality, operation: operation);
        }

        // Conservative: two string operands, no (object) escape -> string equality.
        if (node is BinaryExpressionSyntax fallbackBinary
            && (fallbackBinary.OperatorToken.IsKind(SyntaxKind.EqualsEqualsToken)
                || fallbackBinary.OperatorToken.IsKind(SyntaxKind.ExclamationEqualsToken))
            && !HasObjectEscape(fallbackBinary, model)
            && model.GetTypeInfo(fallbackBinary.Left).Type?.SpecialType == SpecialType.System_String
            && model.GetTypeInfo(fallbackBinary.Right).Type?.SpecialType == SpecialType.System_String)
        {
            var isNotEquals = fallbackBinary.OperatorToken
                .IsKind(SyntaxKind.ExclamationEqualsToken);
            return New(EqualityConcatKind.StringEquality, null,
                isEquality: true, isNotEquals, operation);
        }

        // Same type-based equality fallback as ClassifyByMethod: when the
        // operation is missing entirely (GetOperation returned null or an
        // unrecognized shape), the operand types are still the best signal.
        if (node is BinaryExpressionSyntax eqFallback
            && (eqFallback.OperatorToken.IsKind(SyntaxKind.EqualsEqualsToken)
                || eqFallback.OperatorToken.IsKind(SyntaxKind.ExclamationEqualsToken)))
        {
            var kind = ClassifyEqualityByTypeFallback(eqFallback, model);
            return New(kind, null, isEquality: true, operation: operation);
        }

        return New(EqualityConcatKind.Unknown, operation: operation);
    }

    private static IMethodSymbol? BoundMethodOf(
        IMethodSymbol? operatorMethod,
        SyntaxNode node,
        SemanticModel model)
        => operatorMethod ?? (model.GetSymbolInfo(node).Symbol as IMethodSymbol);

    /// <summary>
    /// Type-only string-concat test for the symbol-less Add fallback
    /// (see <see cref="ClassifyByMethod"/>). A string operand is conclusive:
    /// every predefined <c>string + X</c> / <c>X + string</c> addition is a
    /// concatenation; the result type SpecialType is consulted as a last signal
    /// when operand binding itself failed.
    /// </summary>
    private static bool IsStringConcatByType(BinaryExpressionSyntax node, SemanticModel model)
    {
        if (model.GetTypeInfo(node.Left).Type?.SpecialType == SpecialType.System_String
            || model.GetTypeInfo(node.Right).Type?.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        return model.GetTypeInfo(node).Type?.SpecialType == SpecialType.System_String;
    }

    /// <summary>
    /// Type-only equality fallback for the symbol-less ==/!= site (see
    /// <see cref="ClassifyByMethod"/>). When TPA reference gaps leave both
    /// <c>IBinaryOperation.OperatorMethod</c> and <c>GetSymbolInfo</c> null,
    /// the operand types are still enough to classify the site without
    /// emitting CS2J0470:
    /// <list type="bullet">
    /// <item>Two <see cref="SpecialType.System_String"/> operands, no
    ///   <c>(object)</c> escape → <see cref="EqualityConcatKind.StringEquality"/>.</item>
    /// <item>An explicit <c>(object)</c> escape on either operand, or either
    ///   operand's static type is <see cref="SpecialType.System_Object"/> →
    ///   <see cref="EqualityConcatKind.ObjectIdentity"/> (identity semantics).</item>
    /// <item>Either operand's static type is a delegate
    ///   (<see cref="TypeKind.Delegate"/> or <see cref="SpecialType.System_Delegate"/>)
    ///   → <see cref="EqualityConcatKind.DelegateEquality"/>.</item>
    /// <item>Either operand's type is an <see cref="ErrorType"/> or null
    ///   (unresolvable in the pass compilation) →
    ///   <see cref="EqualityConcatKind.PredefinedOther"/> (pass-through; the
    ///   converter's downstream passes handle the real binding).</item>
    /// <item>Everything else (primitive/enum/struct comparisons, null vs.
    ///   non-string reference type) → <see cref="EqualityConcatKind.PredefinedOther"/>.</item>
    /// </list>
    /// This mirrors the conservative string fallback already present in
    /// <see cref="ClassifyFallback"/> and extends it to every equality shape
    /// that used to fall through to <see cref="EqualityConcatKind.Unknown"/>.
    /// </summary>
    private static EqualityConcatKind ClassifyEqualityByTypeFallback(
        BinaryExpressionSyntax node,
        SemanticModel model)
    {
        var leftType = model.GetTypeInfo(node.Left).Type;
        var rightType = model.GetTypeInfo(node.Right).Type;

        // Unresolvable operand types: TPA reference gap, not a real code issue.
        // Pass through silently; the converter's downstream binding handles it.
        if (IsUnresolvableType(leftType) || IsUnresolvableType(rightType))
            return EqualityConcatKind.PredefinedOther;

        // (object) escape or object-typed operand → reference identity.
        if (HasObjectEscape(node, model)
            || leftType?.SpecialType == SpecialType.System_Object
            || rightType?.SpecialType == SpecialType.System_Object)
            return EqualityConcatKind.ObjectIdentity;

        // Two string operands without (object) escape → string equality.
        if (leftType?.SpecialType == SpecialType.System_String
            && rightType?.SpecialType == SpecialType.System_String)
            return EqualityConcatKind.StringEquality;

        // Delegate-typed operand → delegate equality (the event null-check
        // case is handled earlier in ClassifyByMethod's BuiltinOperator arm;
        // this fallback only fires when the symbol is missing entirely).
        if (IsDelegateContainingType(leftType) || IsDelegateContainingType(rightType))
            return EqualityConcatKind.DelegateEquality;

        // Primitive/enum/struct and null-vs-reference-type comparisons are
        // not this pass's responsibility.
        return EqualityConcatKind.PredefinedOther;
    }

    /// <summary>
    /// True when <paramref name="type"/> is null or an ErrorType — i.e. the
    /// pass compilation could not resolve it. Such sites are not real code
    /// defects; they are TPA reference gaps, so the classifier must not
    /// emit CS2J0470 for them.
    /// </summary>
    private static bool IsUnresolvableType(ITypeSymbol? type)
        => type == null || type.Kind == SymbolKind.ErrorType;

    /// <summary>
    /// A delegate-equality containing type is either a specific delegate type
    /// (<c>delegate</c>-keyword types, incl. System.Action) or the predefined
    /// System.Delegate operator. System.MulticastDelegate binds the object
    /// operator instead (identity), per the M0 probe.
    /// </summary>
    private static bool IsDelegateContainingType(ITypeSymbol? containingType)
        => containingType is { TypeKind: TypeKind.Delegate }
            || containingType?.SpecialType == SpecialType.System_Delegate;

    private static bool HasEventOperand(SyntaxNode node, SemanticModel model)
    {
        if (node is not BinaryExpressionSyntax binary)
            return false;

        foreach (var expression in new[] { binary.Left, binary.Right })
        {
            var operandOperation = model.GetOperation(expression);
            if (operandOperation is IEventReferenceOperation)
                return true;

            if (model.GetSymbolInfo(expression).Symbol is IEventSymbol)
                return true;
        }

        return false;
    }

    private static bool HasEqualityToken(SyntaxNode node)
        => node is BinaryExpressionSyntax tokenBinary
            && (tokenBinary.OperatorToken.IsKind(SyntaxKind.EqualsEqualsToken)
                || tokenBinary.OperatorToken.IsKind(SyntaxKind.ExclamationEqualsToken));

    private static bool HasObjectEscape(BinaryExpressionSyntax binary, SemanticModel model)
    {
        foreach (var operand in new[] { binary.Left, binary.Right })
        {
            if (operand is CastExpressionSyntax cast
                && model.GetTypeInfo(cast.Type).Type?.SpecialType == SpecialType.System_Object)
            {
                return true;
            }

            if (operand.DescendantNodesAndSelf().OfType<CastExpressionSyntax>()
                .Any(c => model.GetTypeInfo(c.Type).Type?.SpecialType == SpecialType.System_Object))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when an operand is <c>(ValueTuple)?</c> — its == null site binds
    /// the object operator but must not be rewritten to ReferenceEquals.
    /// </summary>
    private static bool HasNullableTupleOperand(SyntaxNode node, SemanticModel model)
    {
        if (node is not BinaryExpressionSyntax binary)
            return false;

        foreach (var expression in new[] { binary.Left, binary.Right })
        {
            if (model.GetTypeInfo(expression).Type is INamedTypeSymbol named
                && IsNullableTuple(named))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNullableTuple(INamedTypeSymbol type)
        => type.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && type.TypeArguments.FirstOrDefault() is INamedTypeSymbol inner
            && inner.IsTupleType;

    private static bool IsDynamic(ISymbol? symbol)
        => symbol is ITypeSymbol { TypeKind: TypeKind.Dynamic };

    private static EqualityConcatClassification New(
        EqualityConcatKind kind,
        IMethodSymbol? method = null,
        bool isEquality = false,
        bool isNotEquals = false,
        IOperation? operation = null)
        => new()
        {
            Kind = kind,
            BoundMethod = method,
            IsEquality = isEquality,
            IsNotEquals = isNotEquals,
            Operation = operation,
        };
}
