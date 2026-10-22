using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NBOp = Microsoft.CodeAnalysis.Operations;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// Value domain of the underlying type <c>T</c> of a <c>Nullable&lt;T&gt;</c>
/// lifted site (design §4.2 / decisions 3/19/20). The dimension drives both the
/// guard rendering (M1) and the error routing: C41 special numerics are
/// CS2J5356, decimal is guarded with a bare inner op for C34.
/// </summary>
internal enum NullableValueDomain
{
    /// <summary>The type is not <c>Nullable&lt;T&gt;</c>; classification does not apply.</summary>
    NotNullable,

    /// <summary>Signed and narrow integer domains (sbyte/byte/short/ushort/char/int/long).</summary>
    Integer,

    /// <summary>Unsigned wide domains (uint/ulong); the guard inner op stays true unsigned (E09).</summary>
    Unsigned,

    /// <summary>float/double.</summary>
    Floating,

    /// <summary>decimal — guard produced, inner bare op is owned by C34 (decision 19).</summary>
    Decimal,

    /// <summary>bool — relational/equality/three-valued logic sites.</summary>
    Bool,

    /// <summary>Enum; bit operations are shared with E04 (NL-9 backstop, decision 12).</summary>
    Enum,

    /// <summary>C41 special numeric (nint/nuint/Half/Int128/UInt128; CS2J5356, decision 20).</summary>
    SpecialNumeric,

    /// <summary>Any other value type (custom struct) — lifted operators there are user-defined (decision 21).</summary>
    Other,
}

/// <summary>Flavor of a lifted binary operation site (NL-1/NL-3/NL-4/NL-5).</summary>
internal enum LiftedBinaryFlavor
{
    NotLifted,

    /// <summary>NL-1 arithmetic/bit/shift (<c>+ - * / % &amp; | ^ &lt;&lt; &gt;&gt;</c>) on numeric/enum domains.</summary>
    Arithmetic,

    /// <summary>NL-3 relational (<c>&lt; &gt; &lt;= &gt;=</c>; non-nullable bool result, null → false).</summary>
    Relational,

    /// <summary>NL-4 equality (<c>== !=</c>).</summary>
    Equality,

    /// <summary>NL-5 <c>bool? &amp; bool?</c> (Kleene absorption, decision 6).</summary>
    BoolAnd,

    /// <summary>NL-5 <c>bool? | bool?</c> (Kleene absorption, decision 7).</summary>
    BoolOr,

    /// <summary>NL-5 <c>bool? ^ bool?</c> (plain null propagation, decision 8).</summary>
    BoolXor,

    /// <summary>Lifted but bound to a user-defined operator method (CS2J5353, decision 21).</summary>
    UserDefined,
}

/// <summary>Flavor of a lifted prefix-unary operation site (NL-2/NL-5).</summary>
internal enum LiftedUnaryFlavor
{
    NotLifted,

    /// <summary>NL-2 unary <c>- + ~</c> on numeric/enum domains.</summary>
    Arithmetic,

    /// <summary>NL-5 lifted <c>!</c> on <c>bool?</c>.</summary>
    BoolNot,

    /// <summary>Lifted user-defined unary operator (CS2J5353, decision 21).</summary>
    UserDefined,
}

/// <summary>The four conversion quadrants of decision 13, bound from <see cref="NBOp.IConversionOperation"/>.</summary>
internal enum NullableConversionQuadrant
{
    /// <summary>No nullable conversion involved, or an identity <c>T? → T?</c>.</summary>
    None,

    /// <summary>① <c>T → T?</c> wrap — passes through untouched.</summary>
    Wrap,

    /// <summary>② <c>T? → T</c> explicit unwrap — rewrites to <c>.Value</c> (NL-7).</summary>
    Unwrap,

    /// <summary>③ <c>S? → T?</c> lifted conversion (also <c>S → T?</c> cross-domain) — guarded (NL-8).</summary>
    Lifted,

    /// <summary>④ <c>S? → T</c> unwrap + convert — null throws via <c>.Value</c>.</summary>
    UnwrapAndConvert,

    /// <summary>Lifted user-defined conversion (CS2J5353, decision 21).</summary>
    UserDefined,
}

/// <summary>Classification of one lifted binary site.</summary>
internal sealed record LiftedBinaryClassification(
    LiftedBinaryFlavor Flavor,
    NullableValueDomain Domain,
    bool IsLifted,
    IMethodSymbol? OperatorMethod,
    bool LeftNullable,
    bool RightNullable,
    bool HasNullLiteralOperand)
{
    /// <summary>Built-in lifted (OperatorMethod null or a built-in decimal op) — owned by NL-1/3/4/5.</summary>
    public bool IsBuiltInLifted => IsLifted && Flavor != LiftedBinaryFlavor.UserDefined;
}

/// <summary>Classification of one lifted unary site.</summary>
internal sealed record LiftedUnaryClassification(
    LiftedUnaryFlavor Flavor,
    NullableValueDomain Domain,
    bool IsLifted,
    IMethodSymbol? OperatorMethod,
    bool OperandNullable)
{
    public bool IsBuiltInLifted => IsLifted && Flavor != LiftedUnaryFlavor.UserDefined;
}

/// <summary>
/// The non-lifted raw <c>x == null</c> / <c>x != null</c> shape over a
/// <c>Nullable&lt;T&gt;</c> value-type receiver (Roslyn binds these WITHOUT
/// <see cref="NBOp.IBinaryOperation.IsLifted"/>, so <see cref="ClassifyBinary"/>
/// returns null for them; M1 recognizes the shape with this dedicated
/// semantic+syntactic predicate). Reference-type null comparisons never match.
/// </summary>
internal sealed record NullLiteralEqualityClassification(bool IsNotEquals, ExpressionSyntax NullableSide);

/// <summary>Classification of one nullable conversion site.</summary>
internal sealed record NullableConversionClassification(
    NullableConversionQuadrant Quadrant,
    ITypeSymbol? SourceUnderlying,
    ITypeSymbol? TargetUnderlying,
    bool SourceNullable,
    bool TargetNullable,
    IMethodSymbol? OperatorMethod)
{
    public bool IsLiftedBuiltIn =>
        Quadrant is NullableConversionQuadrant.Unwrap
            or NullableConversionQuadrant.Lifted
            or NullableConversionQuadrant.UnwrapAndConvert
        && Quadrant != NullableConversionQuadrant.UserDefined;
}

/// <summary>
/// M3 decision 17: the C22-owned complex RMW target kinds. A lifted
/// <c>++/--</c>/compound site on one of these shapes is expanded in-rule with
/// the single-evaluation target-temp seam. Everything else classified
/// <see cref="NumericBoundaryAnalyzer.TargetShape.Complex"/> (events,
/// conditional <c>?.</c>/<c>?[]</c>, ref-returning accessors, qualified
/// fields, named/ref indexer arguments) stays CS2J5355.
/// </summary>
internal enum RmwComplexKind
{
    /// <summary>Get/set property (identifier-bound implicit receiver, qualified instance, or static).</summary>
    Property,

    /// <summary>Get/set non-ref indexer (<c>b[i]</c>).</summary>
    Indexer,

    /// <summary>True array element (<c>arr[i]</c>, incl. multidimensional/jagged receivers).</summary>
    ArrayElement,
}

/// <summary>Classification of a lifted increment/compound-assignment target (NL-10/NL-11/decision 15/17).</summary>
internal sealed record LiftedTargetClassification(
    bool IsIncrement,
    bool IsPrefix,
    bool IsCompound,
    bool IsLifted,
    bool IsUserDefined,
    NullableValueDomain Domain,
    NumericBoundaryAnalyzer.TargetShape TargetShape,
    ExpressionSyntax Target,
    RmwComplexKind? ComplexKind = null);

/// <summary>
/// Read-only semantic classifier for C22 (design §4.2). Every decision comes
/// from the <see cref="SemanticModel"/> (<c>GetOperation</c>/<c>GetTypeInfo</c>/
/// <c>GetSymbolInfo</c>); cast text or token spelling is never trusted (hard
/// invariant 4). M0 ships predicates only — the rule performs zero rewrites.
/// </summary>
internal sealed class NullableLiftAnalyzer
{
    private readonly SemanticModel _model;
    private readonly NumericBoundaryOptions _options;
    private readonly NumericBoundaryAnalyzer _shared;

    public NullableLiftAnalyzer(
        SemanticModel model,
        NumericBoundaryOptions options,
        NumericBoundaryAnalyzer shared)
    {
        _model = model;
        _options = options;
        _shared = shared;
    }

    // ── Nullable<T> detection (any T: bool?/enum?/numeric?/custom struct?) ──

    /// <summary>True when <paramref name="type"/> is <c>System.Nullable&lt;T&gt;</c> for any value T.</summary>
    public static bool IsNullableType(ITypeSymbol? type) =>
        type is INamedTypeSymbol named
        && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    /// <summary>
    /// Unwrap <c>Nullable&lt;T&gt;</c> to its underlying T (any value type —
    /// deliberately broader than the shared analyzer's numeric-only private
    /// predicate). Returns false for non-nullable types.
    /// </summary>
    public static bool TryUnwrapNullable(ITypeSymbol? type, out ITypeSymbol? underlying)
    {
        if (IsNullableType(type))
        {
            underlying = ((INamedTypeSymbol)type!).TypeArguments.FirstOrDefault();
            return underlying != null;
        }

        underlying = null;
        return false;
    }

    /// <summary>
    /// Domain of the underlying type of a nullable value type. Returns
    /// <see cref="NullableValueDomain.NotNullable"/> when <paramref name="type"/> is not <c>T?</c>.
    /// </summary>
    public static NullableValueDomain ClassifyDomain(ITypeSymbol? type)
    {
        if (!TryUnwrapNullable(type, out var underlying))
        {
            return NullableValueDomain.NotNullable;
        }

        return ClassifyUnderlying(underlying);
    }

    /// <summary>Domain of a bare (non-nullable) type T.</summary>
    public static NullableValueDomain ClassifyUnderlying(ITypeSymbol? type)
    {
        if (type == null)
        {
            return NullableValueDomain.Other;
        }

        if (type.TypeKind == TypeKind.Enum)
        {
            return NullableValueDomain.Enum;
        }

        if (NumericBoundaryAnalyzer.IsUnsupportedNumericType(type))
        {
            return NullableValueDomain.SpecialNumeric;
        }

        switch (type.SpecialType)
        {
            case SpecialType.System_Boolean:
                return NullableValueDomain.Bool;
            case SpecialType.System_Decimal:
                return NullableValueDomain.Decimal;
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return NullableValueDomain.Floating;
            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
                return NullableValueDomain.Unsigned;
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Char:
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
                return NullableValueDomain.Integer;
            default:
                return NullableValueDomain.Other;
        }
    }

    // ── lifted binary / unary classification (decision 21: OperatorMethod) ──

    /// <summary>True for a built-in lifted binding: OperatorMethod null, or a built-in <c>decimal</c> operator.</summary>
    public static bool IsBuiltInLiftedOperator(IMethodSymbol? operatorMethod) =>
        operatorMethod == null
        || operatorMethod.MethodKind == MethodKind.BuiltinOperator;

    /// <summary>Classify a binary expression; returns null when it is not a lifted binary operation.</summary>
    public LiftedBinaryClassification? ClassifyBinary(BinaryExpressionSyntax node)
    {
        if (_model.GetOperation(node) is not NBOp.IBinaryOperation operation
            || !operation.IsLifted)
        {
            return null;
        }

        // Operand nullability must come from the syntax-level intrinsic type
        // (GetTypeInfo.Type), NOT from operation.LeftOperand.Type: the lifted
        // binding promotes a non-nullable operand (e.g. `a + 1`) to T? before
        // the call, which would make every site look two-sided.
        var leftType = _model.GetTypeInfo(node.Left).Type ?? operation.LeftOperand?.Type;
        var rightType = _model.GetTypeInfo(node.Right).Type ?? operation.RightOperand?.Type;
        var leftNullable = IsNullableType(leftType);
        var rightNullable = IsNullableType(rightType);
        var hasNullLiteral = IsNullLiteralOperation(operation.LeftOperand)
            || IsNullLiteralOperation(operation.RightOperand);

        var domain = ClassifyDomain(leftType) != NullableValueDomain.NotNullable
            ? ClassifyDomain(leftType)
            : ClassifyDomain(rightType);

        if (operation.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator })
        {
            return new LiftedBinaryClassification(
                LiftedBinaryFlavor.UserDefined, domain, IsLifted: true,
                operation.OperatorMethod, leftNullable, rightNullable, hasNullLiteral);
        }

        var flavor = ClassifyBinaryFlavor(operation.OperatorKind, domain);
        return new LiftedBinaryClassification(
            flavor, domain, IsLifted: true, operation.OperatorMethod,
            leftNullable, rightNullable, hasNullLiteral);
    }

    private static LiftedBinaryFlavor ClassifyBinaryFlavor(
        NBOp.BinaryOperatorKind kind, NullableValueDomain domain)
    {
        switch (kind)
        {
            case NBOp.BinaryOperatorKind.LessThan:
            case NBOp.BinaryOperatorKind.LessThanOrEqual:
            case NBOp.BinaryOperatorKind.GreaterThan:
            case NBOp.BinaryOperatorKind.GreaterThanOrEqual:
                return LiftedBinaryFlavor.Relational;
            case NBOp.BinaryOperatorKind.Equals:
            case NBOp.BinaryOperatorKind.NotEquals:
                return LiftedBinaryFlavor.Equality;
            case NBOp.BinaryOperatorKind.And when domain == NullableValueDomain.Bool:
                return LiftedBinaryFlavor.BoolAnd;
            case NBOp.BinaryOperatorKind.Or when domain == NullableValueDomain.Bool:
                return LiftedBinaryFlavor.BoolOr;
            case NBOp.BinaryOperatorKind.ExclusiveOr when domain == NullableValueDomain.Bool:
                return LiftedBinaryFlavor.BoolXor;
            default:
                return LiftedBinaryFlavor.Arithmetic;
        }
    }

    /// <summary>Classify a prefix-unary expression; returns null when it is not lifted unary.</summary>
    public LiftedUnaryClassification? ClassifyUnary(PrefixUnaryExpressionSyntax node)
    {
        if (_model.GetOperation(node) is not NBOp.IUnaryOperation operation
            || !operation.IsLifted)
        {
            return null;
        }

        var operandType = operation.Operand?.Type;
        var operandNullable = IsNullableType(operandType);
        var domain = ClassifyDomain(operandType);

        if (operation.OperatorMethod is { MethodKind: MethodKind.UserDefinedOperator })
        {
            return new LiftedUnaryClassification(
                LiftedUnaryFlavor.UserDefined, domain, IsLifted: true,
                operation.OperatorMethod, operandNullable);
        }

        var flavor = operation.OperatorKind == NBOp.UnaryOperatorKind.Not
            && domain == NullableValueDomain.Bool
                ? LiftedUnaryFlavor.BoolNot
                : LiftedUnaryFlavor.Arithmetic;
        return new LiftedUnaryClassification(
            flavor, domain, IsLifted: true, operation.OperatorMethod, operandNullable);
    }

    // ── conversion four quadrants (decision 13) ──

    /// <summary>
    /// Classify the conversion bound to a syntax node (typically a
    /// <see cref="CastExpressionSyntax"/>). Returns a <see cref="NullableConversionQuadrant.None"/>
    /// record for ordinary non-nullable conversions; never returns null so
    /// callers can assert the quadrant uniformly.
    /// </summary>
    public NullableConversionClassification ClassifyConversion(ExpressionSyntax node)
    {
        if (_model.GetOperation(node) is NBOp.IConversionOperation conversion)
        {
            return ClassifyConversionOperation(conversion);
        }

        // Some implicit conversions surface on the inner expression's
        // converted type rather than as a conversion operation of the node.
        var typeInfo = _model.GetTypeInfo(node);
        return ClassifyFromTypes(typeInfo.Type, typeInfo.ConvertedType, operatorMethod: null);
    }

    /// <summary>Quadrant decision straight from the bound conversion operation (decision 13).</summary>
    public static NullableConversionClassification ClassifyConversionOperation(
        NBOp.IConversionOperation conversion)
    {
        var source = conversion.Operand?.Type;
        var target = conversion.Type;
        return ClassifyFromTypes(source, target, conversion.OperatorMethod);
    }

    private static NullableConversionClassification ClassifyFromTypes(
        ITypeSymbol? source, ITypeSymbol? target, IMethodSymbol? operatorMethod)
    {
        var sourceNullable = TryUnwrapNullable(source, out var sourceUnderlying);
        var targetNullable = TryUnwrapNullable(target, out var targetUnderlying);

        // Roslyn 5 binds user-defined conversions (lifted or plain) with a
        // non-null OperatorMethod whose MethodKind is Conversion (not
        // UserDefinedOperator); built-in numeric/enum conversions carry no
        // method symbol at all, so any method symbol means user-defined.
        if (operatorMethod != null)
        {
            return new NullableConversionClassification(
                NullableConversionQuadrant.UserDefined,
                sourceUnderlying, targetUnderlying,
                sourceNullable, targetNullable, operatorMethod);
        }

        NullableConversionQuadrant quadrant;
        if (!sourceNullable && targetNullable)
        {
            // 引用类型（含 object/dynamic/接口）→ T? 是拆箱/引用转换，null 必须
            // 保持 null；它不是值类型的 lifted 转换（S → T?）。这里保持源语法，
            // 后续 Java 侧的普通 cast 对 null 天然返回 null。
            if (source != null && !source.IsValueType)
            {
                quadrant = NullableConversionQuadrant.None;
            }
            else
            {
                // T → T? wrap; a null literal cast (source type unknown) is a wrap
                // too. A cross-domain S → T? binds the lifted conversion with a
                // non-nullable operand (decision 13-③ family).
                quadrant = source == null
                    || SymbolEqualityComparer.Default.Equals(source, targetUnderlying)
                        ? NullableConversionQuadrant.Wrap
                        : NullableConversionQuadrant.Lifted;
            }
        }
        else if (sourceNullable && !targetNullable)
        {
            // Boxing a T? to object/interface (or any reference target) is a
            // nullable boxing conversion, not an NL-7 unwrap: null must remain
            // null instead of forcing a .Value read.
            if (target is not { IsValueType: true })
            {
                quadrant = NullableConversionQuadrant.None;
            }
            else
            {
                quadrant = SymbolEqualityComparer.Default.Equals(sourceUnderlying, target)
                    ? NullableConversionQuadrant.Unwrap
                    : NullableConversionQuadrant.UnwrapAndConvert;
            }
        }
        else if (sourceNullable && targetNullable)
        {
            quadrant = SymbolEqualityComparer.Default.Equals(sourceUnderlying, targetUnderlying)
                ? NullableConversionQuadrant.None
                : NullableConversionQuadrant.Lifted;
        }
        else
        {
            quadrant = NullableConversionQuadrant.None;
        }

        return new NullableConversionClassification(
            quadrant, sourceUnderlying, targetUnderlying,
            sourceNullable, targetNullable, operatorMethod);
    }

    // ── lifted increment / compound target (decisions 15/17) ──

    /// <summary>
    /// Classify a lifted <c>++/--</c> or compound-assignment site. Returns null
    /// for non-lifted nodes. The simple/complex target decision reuses the
    /// shared C15/C17 <see cref="NumericBoundaryAnalyzer.ClassifyTarget"/>
    /// (LocalOrParameter/Field are NL-10/11 simple targets). A Complex target
    /// is refined by <see cref="ClassifyComplexRmwTarget"/> in M3: owned
    /// property/indexer/array shapes carry a <see cref="RmwComplexKind"/>,
    /// every other complex shape stays CS2J5355 (decision 17 closure).
    /// </summary>
    public LiftedTargetClassification? ClassifyLiftedTarget(ExpressionSyntax node)
    {
        switch (_model.GetOperation(node))
        {
            case NBOp.IIncrementOrDecrementOperation increment when increment.IsLifted:
            {
                var targetSyntax = (increment.Target?.Syntax as ExpressionSyntax) ?? node;
                return new LiftedTargetClassification(
                    IsIncrement: true,
                    IsPrefix: !increment.IsPostfix,
                    IsCompound: false,
                    IsLifted: true,
                    IsUserDefined: increment.OperatorMethod
                        is { MethodKind: MethodKind.UserDefinedOperator },
                    Domain: ClassifyDomain(increment.Target?.Type),
                    TargetShape: _shared.ClassifyTarget(targetSyntax),
                    Target: targetSyntax,
                    ComplexKind: ClassifyComplexRmwTarget(targetSyntax));
            }

            case NBOp.ICompoundAssignmentOperation compound when compound.IsLifted:
            {
                var targetSyntax = node is AssignmentExpressionSyntax assignment
                    ? assignment.Left
                    : (compound.Target?.Syntax as ExpressionSyntax) ?? node;
                return new LiftedTargetClassification(
                    IsIncrement: false,
                    IsPrefix: false,
                    IsCompound: true,
                    IsLifted: true,
                    IsUserDefined: compound.OperatorMethod
                        is { MethodKind: MethodKind.UserDefinedOperator },
                    Domain: ClassifyDomain(compound.Target?.Type),
                    TargetShape: _shared.ClassifyTarget(targetSyntax),
                    Target: targetSyntax,
                    ComplexKind: ClassifyComplexRmwTarget(targetSyntax));
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// M3 decision 17: refine a <see cref="NumericBoundaryAnalyzer.TargetShape.Complex"/>
    /// lifted RMW target into a C22-owned <see cref="RmwComplexKind"/>
    /// (get/set non-ref property incl. static, get/set non-ref positional
    /// indexer, true array element) or null when the site must stay
    /// CS2J5355 — events, conditional-access (<c>?.</c>/<c>?[]</c>) targets,
    /// ref-returning accessors, qualified fields, invocations and indexers
    /// with named/<c>ref</c>/<c>out</c>/<c>in</c> arguments.
    /// </summary>
    public RmwComplexKind? ClassifyComplexRmwTarget(ExpressionSyntax target)
    {
        target = NullableLiftSyntaxFacts.UnwrapParens(target);

        switch (target)
        {
            case IdentifierNameSyntax:
            case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression }:
            {
                var symbol = _model.GetSymbolInfo(target).Symbol;
                if (symbol is IEventSymbol)
                {
                    return null;
                }

                if (symbol is IPropertySymbol property
                    && !property.IsIndexer
                    && property.RefKind == RefKind.None
                    && property.GetMethod != null
                    && property.SetMethod is { IsInitOnly: false })
                {
                    return RmwComplexKind.Property;
                }

                // Qualified fields and any other member shape stay CS2J5355.
                return null;
            }

            case ElementAccessExpressionSyntax elementAccess:
            {
                // Named/ref-style index arguments cannot be replayed
                // positionally from hoisted temps; stay CS2J5355.
                foreach (var argument in elementAccess.ArgumentList.Arguments)
                {
                    if (argument.NameColon != null
                        || argument.RefKindKeyword.Kind() != SyntaxKind.None)
                    {
                        return null;
                    }
                }

                var symbol = _model.GetSymbolInfo(target).Symbol;
                if (symbol is IPropertySymbol indexer
                    && indexer is { IsIndexer: true, RefKind: RefKind.None }
                    && indexer.GetMethod != null
                    && indexer.SetMethod is { IsInitOnly: false })
                {
                    return RmwComplexKind.Indexer;
                }

                if (_model.GetTypeInfo(elementAccess.Expression).Type is IArrayTypeSymbol)
                {
                    return RmwComplexKind.ArrayElement;
                }

                return null;
            }

            default:
                // ConditionalAccess/MemberBinding (?., ?[]), invocations and
                // every other storage shape stay CS2J5355.
                return null;
        }
    }

    // ── GetValueOrDefault (NL-6) ──

    /// <summary>
    /// True when <paramref name="node"/> is <c>Nullable&lt;T&gt;.GetValueOrDefault()</c>
    /// (or the one-argument <c>GetValueOrDefault(d)</c> overload).
    /// </summary>
    public bool IsGetValueOrDefault(
        InvocationExpressionSyntax node, out bool hasDefaultArgument)
    {
        hasDefaultArgument = false;
        if (_model.GetOperation(node) is not NBOp.IInvocationOperation invocation)
        {
            return false;
        }

        var method = invocation.TargetMethod;
        if (method == null
            || method.Name != nameof(Nullable<int>.GetValueOrDefault)
            || !IsNullableType(method.ContainingType))
        {
            return false;
        }

        hasDefaultArgument = invocation.Arguments.Length > 0;
        return true;
    }

    // ── dynamic / expression-tree (CS2J5352/CS2J5354, decision 22) ──

    /// <summary>True when the operation result or either operand is dynamically bound.</summary>
    public static bool IsDynamicOperation(IOperation? operation)
    {
        static bool IsDynamic(ITypeSymbol? type) => type?.TypeKind == TypeKind.Dynamic;

        switch (operation)
        {
            case NBOp.IBinaryOperation binary:
                return IsDynamic(binary.Type)
                    || IsDynamic(binary.LeftOperand?.Type)
                    || IsDynamic(binary.RightOperand?.Type);
            case NBOp.IUnaryOperation unary:
                return IsDynamic(unary.Type) || IsDynamic(unary.Operand?.Type);
            case NBOp.IIncrementOrDecrementOperation increment:
                return IsDynamic(increment.Target?.Type);
            case NBOp.ICompoundAssignmentOperation compound:
                return IsDynamic(compound.Target?.Type)
                    || IsDynamic(compound.Value?.Type);
            case NBOp.IConversionOperation conversion:
                return IsDynamic(conversion.Type) || IsDynamic(conversion.Operand?.Type);
            default:
                return operation != null && IsDynamic(operation.Type);
        }
    }

    /// <summary>True inside an <c>Expression&lt;TDelegate&gt;</c> tree (delegates to the shared analyzer).</summary>
    public bool IsWithinExpressionTree(SyntaxNode node) => _shared.IsWithinExpressionTree(node);

    // ── stable references (decision 2; ported verbatim in spirit from E04 EnumSyntaxRewriter.IsStableReference) ──

    /// <summary>
    /// Stable reference predicate (pure relative to the bound semantic model):
    /// locals/parameters and fields reached through a side-effect-free receiver
    /// chain (<c>f</c>, <c>this.f</c>, <c>Type.f</c>, <c>outer.f</c> where every
    /// receiver is a local/parameter/field/type). Properties, indexers, calls,
    /// conditional accesses and ternaries are unstable and must be hoisted once.
    /// Ported from EnumSyntaxRewriter.IsStableReference (L146-173) as a
    /// semantic-model function; M2 may hoist it to a shared fact (tech debt).
    /// </summary>
    public bool IsStableReference(ExpressionSyntax expression)
    {
        expression = NullableLiftSyntaxFacts.UnwrapParens(expression);

        // During the bottom-up rewrite a visited operand can be a freshly
        // synthesized node (e.g. an inner site's guard conditional) that is
        // detached from the original tree this model is bound to; semantic
        // queries throw on it, and such a node is definitionally unstable and
        // must be hoisted (decision 14).
        if (expression.SyntaxTree != _model.SyntaxTree)
        {
            return false;
        }

        // Constants are side-effect-free and need no temp regardless of the
        // number of times the guard evaluates them.
        if (expression is LiteralExpressionSyntax or DefaultExpressionSyntax)
        {
            return true;
        }

        if (expression is IdentifierNameSyntax)
        {
            return _model.GetSymbolInfo(expression).Symbol is ILocalSymbol
                or IParameterSymbol
                or IFieldSymbol;
        }

        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (_model.GetSymbolInfo(expression).Symbol is not IFieldSymbol)
            {
                return false;
            }

            return IsStableReceiver(memberAccess.Expression);
        }

        return false;
    }

    /// <summary>
    /// Side-effect-free receiver predicate (M3 decision 17): <c>this</c>/
    /// <c>base</c>, a local/parameter/field/type identifier, or a field/type
    /// member chain — safe to repeat as a property/indexer receiver without a
    /// single-evaluation temp.
    /// </summary>
    public bool IsStableReceiver(ExpressionSyntax expression)
    {
        expression = NullableLiftSyntaxFacts.UnwrapParens(expression);

        if (expression is ThisExpressionSyntax or BaseExpressionSyntax)
        {
            return true;
        }

        if (expression is IdentifierNameSyntax)
        {
            var symbol = _model.GetSymbolInfo(expression).Symbol;
            // Field/local/parameter receiver, or a type name (static owner).
            return symbol is IFieldSymbol
                or ILocalSymbol
                or IParameterSymbol
                or INamedTypeSymbol;
        }

        if (expression is MemberAccessExpressionSyntax nested)
        {
            var symbol = _model.GetSymbolInfo(expression).Symbol;
            return symbol is IFieldSymbol or INamedTypeSymbol
                && IsStableReceiver(nested.Expression);
        }

        return false;
    }

    // ── E04 product identification (§3.2 / risk 3) ──

    /// <summary>
    /// True for the E04/guard product shape: a <c>?:</c> conditional is never a
    /// lifted operator node. Binding is operation-based, so rewritten guards
    /// naturally classify as <see cref="NBOp.IConditionalOperation"/> and are
    /// never re-lifted (idempotency / no double wrapping).
    /// </summary>
    public static bool IsConditionalProduct(IOperation? operation) =>
        operation is NBOp.IConditionalOperation;

    /// <summary>
    /// True when <paramref name="node"/> itself is a conditional expression
    /// (E04 lifted-enum ternary or an already C22-guarded site).
    /// </summary>
    public static bool IsConditionalProductSyntax(SyntaxNode node) =>
        node is ConditionalExpressionSyntax;

    // ── M0 potential-site tally (local only; stats buckets stay zero) ──

    /// <summary>
    /// Read-only walk counting potential lifted sites the rule will own in
    /// M1/M2. M0 callers discard the result; the walk exists to exercise the
    /// predicates tree-wide (and is the core of the future NL-14 residual
    /// scanner wiring).
    /// </summary>
    public NullablePotentialSiteCount CountPotentialSites(SyntaxNode root)
    {
        int binary = 0, unary = 0, relational = 0, equality = 0, boolLogic = 0;
        int gov = 0, unwrap = 0, liftedConversion = 0;
        int increment = 0, compound = 0, enumBackstop = 0;
        int dynamic = 0, expressionTree = 0, userDefined = 0, specialNumeric = 0;

        foreach (var node in root.DescendantNodes())
        {
            var operation = _model.GetOperation(node);
            if (operation == null)
            {
                continue;
            }

            if (IsDynamicOperation(operation))
            {
                dynamic++;
                continue;
            }

            switch (operation)
            {
                case NBOp.IBinaryOperation binaryOperation when binaryOperation.IsLifted:
                {
                    var classification = node is BinaryExpressionSyntax syntax
                        ? ClassifyBinary(syntax)
                        : null;
                    if (classification == null)
                    {
                        break;
                    }

                    if (IsWithinExpressionTree(node))
                    {
                        expressionTree++;
                    }
                    else if (classification.Flavor == LiftedBinaryFlavor.UserDefined)
                    {
                        userDefined++;
                    }
                    else
                    {
                        binary++;
                        switch (classification.Flavor)
                        {
                            case LiftedBinaryFlavor.Relational: relational++; break;
                            case LiftedBinaryFlavor.Equality: equality++; break;
                            case LiftedBinaryFlavor.BoolAnd:
                            case LiftedBinaryFlavor.BoolOr:
                            case LiftedBinaryFlavor.BoolXor:
                                boolLogic++; break;
                        }

                        if (classification.Domain == NullableValueDomain.SpecialNumeric)
                        {
                            specialNumeric++;
                        }
                        else if (classification.Domain == NullableValueDomain.Enum)
                        {
                            enumBackstop++;
                        }
                    }

                    break;
                }

                case NBOp.IUnaryOperation unaryOperation when unaryOperation.IsLifted
                    && node is PrefixUnaryExpressionSyntax unarySyntax:
                {
                    var classification = ClassifyUnary(unarySyntax);
                    if (classification == null)
                    {
                        break;
                    }

                    if (IsWithinExpressionTree(node))
                    {
                        expressionTree++;
                    }
                    else if (classification.Flavor == LiftedUnaryFlavor.UserDefined)
                    {
                        userDefined++;
                    }
                    else
                    {
                        unary++;
                        if (classification.Flavor == LiftedUnaryFlavor.BoolNot)
                        {
                            boolLogic++;
                        }

                        if (classification.Domain == NullableValueDomain.Enum)
                        {
                            enumBackstop++;
                        }
                    }

                    break;
                }

                case NBOp.IIncrementOrDecrementOperation { IsLifted: true }:
                case NBOp.ICompoundAssignmentOperation { IsLifted: true }:
                {
                    var site = node as ExpressionSyntax;
                    var classification = site == null ? null : ClassifyLiftedTarget(site);
                    if (classification == null)
                    {
                        break;
                    }

                    if (IsWithinExpressionTree(node))
                    {
                        expressionTree++;
                    }
                    else if (classification.IsUserDefined)
                    {
                        userDefined++;
                    }
                    else if (classification.IsCompound)
                    {
                        compound++;
                    }
                    else
                    {
                        increment++;
                    }

                    if (classification.Domain == NullableValueDomain.SpecialNumeric)
                    {
                        specialNumeric++;
                    }

                    break;
                }

                case NBOp.IInvocationOperation when node is InvocationExpressionSyntax invocationSyntax:
                    if (IsGetValueOrDefault(invocationSyntax, out _))
                    {
                        gov++;
                    }

                    break;

                case NBOp.IConversionOperation conversion when node is ExpressionSyntax expressionSyntax:
                {
                    var classification = ClassifyConversion(expressionSyntax);
                    switch (classification.Quadrant)
                    {
                        case NullableConversionQuadrant.Unwrap:
                        case NullableConversionQuadrant.UnwrapAndConvert:
                            unwrap++;
                            break;
                        case NullableConversionQuadrant.Lifted:
                            liftedConversion++;
                            break;
                    }

                    break;
                }
            }
        }

        return new NullablePotentialSiteCount(
            binary, unary, relational, equality, boolLogic,
            gov, unwrap, liftedConversion,
            increment, compound, enumBackstop,
            dynamic, expressionTree, userDefined, specialNumeric);
    }

    private static bool IsNullLiteralOperation(IOperation? operation) =>
        operation is NBOp.ILiteralOperation { ConstantValue.HasValue: true } literal
        && literal.ConstantValue.Value is null;

    /// <summary>
    /// True when the operation ultimately evaluates to a null constant,
    /// looking THROUGH injected conversions — <c>(int?)null</c> binds as a
    /// conversion wrapping a null literal and behaves like the null operand
    /// of a lifted site.
    /// </summary>
    public static bool IsNullConstant(IOperation? operation)
    {
        while (operation is NBOp.IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return IsNullLiteralOperation(operation);
    }

    /// <summary>
    /// Classify the dedicated raw null-literal equality shape
    /// (<c>x == null</c>, <c>null != x</c>): an ==/!= binary that Roslyn
    /// binds NON-lifted, one syntactic side is a bare <c>null</c> literal and
    /// the other side's static type is <c>Nullable&lt;T&gt;</c>. Returns null
    /// for lifted cast-null forms (<c>x == (int?)null</c> — those flow
    /// through <see cref="ClassifyBinary"/>) and for reference-type null
    /// comparisons (<c>string? s; s == null</c>).
    /// </summary>
    public NullLiteralEqualityClassification? ClassifyNullLiteralEquality(BinaryExpressionSyntax node)
    {
        if (node.Kind() is not (SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression))
        {
            return null;
        }

        if (_model.GetOperation(node) is not NBOp.IBinaryOperation operation || operation.IsLifted)
        {
            return null;
        }

        var leftIsNull = IsRawNullLiteralSyntax(node.Left) && IsNullLiteralOperation(operation.LeftOperand);
        var rightIsNull = IsRawNullLiteralSyntax(node.Right) && IsNullLiteralOperation(operation.RightOperand);
        if (leftIsNull == rightIsNull)
        {
            return null;
        }

        var nullableSide = leftIsNull ? node.Right : node.Left;
        var sideType = _model.GetTypeInfo(nullableSide).Type;
        if (!IsNullableType(sideType))
        {
            return null;
        }

        return new NullLiteralEqualityClassification(
            node.IsKind(SyntaxKind.NotEqualsExpression), nullableSide);
    }

    private static bool IsRawNullLiteralSyntax(ExpressionSyntax expression) =>
        NullableLiftSyntaxFacts.UnwrapParens(expression)
            is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.NullLiteralExpression);
}

/// <summary>Tree-wide potential-site tally (M0 local-only; never written to stats).</summary>
internal sealed record NullablePotentialSiteCount(
    int Binary,
    int Unary,
    int Relational,
    int Equality,
    int BoolLogic,
    int GetValueOrDefault,
    int UnwrapConversion,
    int LiftedConversion,
    int Increment,
    int Compound,
    int EnumBackstop,
    int Dynamic,
    int ExpressionTree,
    int UserDefined,
    int SpecialNumeric)
{
    /// <summary>Total lifted operator/member sites observed (excluding dynamic/expression-tree/user-defined deferrals).</summary>
    public int TotalOwned =>
        Binary + Unary + GetValueOrDefault
        + UnwrapConversion + LiftedConversion
        + Increment + Compound;
}
