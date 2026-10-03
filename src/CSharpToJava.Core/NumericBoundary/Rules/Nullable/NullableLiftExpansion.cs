using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.Nullable;

/// <summary>
/// C22 M1 pure codegen (design §4.4 / decisions 3-8): renders the explicit
/// guard shapes for lifted <c>Nullable&lt;T&gt;</c> operators. Every method is
/// side-effect free and takes only bound syntax/symbols; the Pass1 rewriter
/// (<see cref="NullableLiftRewriter"/>) owns classification, single-evaluation
/// hoisting and annotation attachment.
/// <para>
/// Operand access is abstracted via <see cref="OperandAccess"/>: a stable
/// reference is read directly (safe to repeat in the guard), a hoisted
/// unstable operand reads from a temp identifier, and a semantically-null
/// constant (<c>null</c> or <c>(T?)null</c>) contributes <c>false</c> to
/// HasValue chains and <c>default(T)</c> to the (dead) value branch.
/// </para>
/// </summary>
internal static class NullableLiftExpansion
{
    /// <summary>
    /// NL-1 lifted binary arithmetic/bit/shift guard (decision 3):
    /// <c>(a.HasValue &amp;&amp; b.HasValue) ? (T?)(a.Value op b.Value) : (T?)null</c>.
    /// Also used for NL-5 lifted <c>bool? ^</c> (decision 8 — plain null
    /// propagation) with <paramref name="underlying"/> = <c>bool</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedBinaryGuard(
        SyntaxKind binaryKind,
        SyntaxKind operatorKind,
        OperandAccess left,
        OperandAccess right,
        ITypeSymbol underlying)
    {
        var nullableType = NullableTypeSyntax(underlying);
        var valueType = UnderlyingTypeSyntax(underlying);
        var condition = AllHaveValues(left, right);
        var inner = BinaryExpression(
            binaryKind,
            left.ValueExpression(valueType),
            Token(operatorKind),
            right.ValueExpression(valueType));
        var conditional = Guard(ConditionalExpression(
            condition,
            CastNullable(nullableType, ParenthesizedExpression(inner)),
            CastNull(nullableType)));
        return ParenthesizedExpression(conditional);
    }

    /// <summary>
    /// NL-2 lifted unary arithmetic guard (decision 3):
    /// <c>a.HasValue ? (T?)(-a.Value) : (T?)null</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedUnaryGuard(
        SyntaxKind unaryKind,
        SyntaxKind operatorKind,
        OperandAccess operand,
        ITypeSymbol underlying)
    {
        var nullableType = NullableTypeSyntax(underlying);
        var valueType = UnderlyingTypeSyntax(underlying);
        var inner = PrefixUnaryExpression(unaryKind, Token(operatorKind), operand.ValueExpression(valueType));
        var conditional = Guard(ConditionalExpression(
            operand.HasValueExpression(),
            CastNullable(nullableType, ParenthesizedExpression(inner)),
            CastNull(nullableType)));
        return ParenthesizedExpression(conditional);
    }

    /// <summary>
    /// NL-3 lifted relational guard (decision 4):
    /// <c>a.HasValue &amp;&amp; b.HasValue &amp;&amp; a.Value op b.Value</c> —
    /// any null operand yields <c>false</c> (including <c>null &gt;= null</c>);
    /// the result is a non-nullable <c>bool</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedRelationalGuard(
        SyntaxKind binaryKind,
        SyntaxKind operatorKind,
        OperandAccess left,
        OperandAccess right,
        ITypeSymbol underlying)
    {
        var valueType = UnderlyingTypeSyntax(underlying);
        var comparison = BinaryExpression(
            binaryKind,
            left.ValueExpression(valueType),
            Token(operatorKind),
            right.ValueExpression(valueType));
        var chain = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            AllHaveValues(left, right),
            comparison);
        return ParenthesizedExpression(chain);
    }

    /// <summary>
    /// NL-4 lifted equality for two <c>T?</c> positions (decision 5):
    /// <c>a.HasValue ? (b.HasValue &amp;&amp; a.Value == b.Value) : !b.HasValue</c>;
    /// <c>!=</c> negates the whole form. A semantically-null constant operand
    /// (<c>(T?)null</c>) participates through <see cref="OperandAccess"/>
    /// (<c>HasValue=false</c>), so the double-null cases fall out naturally.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedEqualityBothNullable(
        bool isNotEquals,
        OperandAccess left,
        OperandAccess right,
        ITypeSymbol underlying)
    {
        var valueType = UnderlyingTypeSyntax(underlying);
        // `!=` is the whole-form negation of `==` (decision 5); the inner
        // comparison ALWAYS stays `==` — negating both would double-invert.
        var equality = BinaryExpression(
            SyntaxKind.EqualsExpression,
            left.ValueExpression(valueType),
            Token(SyntaxKind.EqualsEqualsToken),
            right.ValueExpression(valueType));
        var bothHaveValue = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            right.HasValueExpression(),
            equality);
        var conditional = Guard(ConditionalExpression(
            left.HasValueExpression(),
            ParenthesizedExpression(bothHaveValue),
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                right.HasValueExpression())));
        var expression = isNotEquals
            ? PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                ParenthesizedExpression(conditional))
            : (ExpressionSyntax)conditional;
        return ParenthesizedExpression(expression);
    }

    /// <summary>
    /// NL-4 lifted equality with exactly one <c>T?</c> position (decision 5):
    /// <c>a.HasValue &amp;&amp; a.Value == b</c>; <c>!=</c> negates the form.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedEqualityOneSided(
        bool isNotEquals,
        OperandAccess nullableSide,
        OperandAccess otherSide,
        ITypeSymbol underlying)
    {
        var valueType = UnderlyingTypeSyntax(underlying);
        // `!=` is the whole-form negation of `==` (decision 5); the inner
        // comparison ALWAYS stays `==` — negating both would double-invert.
        var equality = BinaryExpression(
            SyntaxKind.EqualsExpression,
            nullableSide.ValueExpression(valueType),
            Token(SyntaxKind.EqualsEqualsToken),
            otherSide.ValueExpression(valueType));
        var chain = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            nullableSide.HasValueExpression(),
            equality);
        var expression = isNotEquals
            ? PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                ParenthesizedExpression(chain))
            : (ExpressionSyntax)chain;
        return ParenthesizedExpression(expression);
    }

    /// <summary>
    /// NL-4 raw <c>null</c>-literal comparison (Roslyn binds <c>x == null</c>
    /// as a NON-lifted null check): <c>x == null → !x.HasValue</c>,
    /// <c>x != null → x.HasValue</c> (decision 5).
    /// </summary>
    public static ParenthesizedExpressionSyntax NullLiteralEquality(
        OperandAccess nullableSide, bool isNotEquals)
    {
        var expression = isNotEquals
            ? nullableSide.HasValueExpression()
            : PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                nullableSide.HasValueExpression());
        return ParenthesizedExpression(expression);
    }

    /// <summary>
    /// NL-5 lifted <c>bool? &amp;</c> Kleene three-valued conjunction
    /// (decision 6): either side a definite <c>false</c> absorbs to
    /// <c>false</c>; otherwise any null yields <c>null</c>; else <c>true</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedBoolAnd(
        OperandAccess left, OperandAccess right)
    {
        var boolNullable = SyntaxFactory.NullableType(SpecialTypeSyntax(SpecialType.System_Boolean));

        var leftFalse = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            left.HasValueExpression(),
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                left.ValueExpression(SpecialTypeSyntax(SpecialType.System_Boolean))));
        var rightFalse = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            right.HasValueExpression(),
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                right.ValueExpression(SpecialTypeSyntax(SpecialType.System_Boolean))));
        var absorbsFalse = BinaryExpression(SyntaxKind.LogicalOrExpression, leftFalse, rightFalse);

        var eitherNull = BinaryExpression(
            SyntaxKind.LogicalOrExpression,
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                left.HasValueExpression()),
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                right.HasValueExpression()));
        var nullOrTrue = Guard(ConditionalExpression(
            eitherNull,
            CastNull(boolNullable),
            CastNullable(boolNullable, LiteralExpression(SyntaxKind.TrueLiteralExpression))));
        // The absorption literal carries the explicit (bool?) cast: without it
        // the Java translation emits a primitive/boxed mixed ternary whose
        // null branch is unboxed and NPEs.
        var whole = Guard(ConditionalExpression(
            absorbsFalse,
            CastNullable(boolNullable, LiteralExpression(SyntaxKind.FalseLiteralExpression)),
            nullOrTrue));
        return ParenthesizedExpression(whole);
    }

    /// <summary>
    /// NL-5 lifted <c>bool? |</c> Kleene three-valued disjunction
    /// (decision 7): either side a definite <c>true</c> absorbs to
    /// <c>true</c>; otherwise any null yields <c>null</c>; else <c>false</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedBoolOr(
        OperandAccess left, OperandAccess right)
    {
        var boolNullable = SyntaxFactory.NullableType(SpecialTypeSyntax(SpecialType.System_Boolean));

        var leftTrue = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            left.HasValueExpression(),
            left.ValueExpression(SpecialTypeSyntax(SpecialType.System_Boolean)));
        var rightTrue = BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            right.HasValueExpression(),
            right.ValueExpression(SpecialTypeSyntax(SpecialType.System_Boolean)));
        var absorbsTrue = BinaryExpression(SyntaxKind.LogicalOrExpression, leftTrue, rightTrue);

        var eitherNull = BinaryExpression(
            SyntaxKind.LogicalOrExpression,
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                left.HasValueExpression()),
            PrefixUnaryExpression(
                SyntaxKind.LogicalNotExpression,
                Token(SyntaxKind.ExclamationToken),
                right.HasValueExpression()));
        var nullOrFalse = Guard(ConditionalExpression(
            eitherNull,
            CastNull(boolNullable),
            CastNullable(boolNullable, LiteralExpression(SyntaxKind.FalseLiteralExpression))));
        // The absorption literal carries the explicit (bool?) cast (see
        // LiftedBoolAnd): keeps the Java ternary on a single boxed type so the
        // null branch is never unboxed.
        var whole = Guard(ConditionalExpression(
            absorbsTrue,
            CastNullable(boolNullable, LiteralExpression(SyntaxKind.TrueLiteralExpression)),
            nullOrFalse));
        return ParenthesizedExpression(whole);
    }

    /// <summary>
    /// NL-5 lifted <c>bool? !</c> (decision 8):
    /// <c>a.HasValue ? (bool?)!a.Value : (bool?)null</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedBoolNot(OperandAccess operand)
    {
        var boolType = SpecialTypeSyntax(SpecialType.System_Boolean);
        var boolNullable = SyntaxFactory.NullableType(boolType);
        var inner = PrefixUnaryExpression(
            SyntaxKind.LogicalNotExpression,
            Token(SyntaxKind.ExclamationToken),
            operand.ValueExpression(boolType));
        var conditional = Guard(ConditionalExpression(
            operand.HasValueExpression(),
            CastNullable(boolNullable, ParenthesizedExpression(inner)),
            CastNull(boolNullable)));
        return ParenthesizedExpression(conditional);
    }

    // ── NL-6 GetValueOrDefault (decision 10) ──

    /// <summary>
    /// NL-6 zero-argument GOV (decision 10):
    /// <c>x.GetValueOrDefault() → (x.HasValue ? x.Value : default(T))</c>.
    /// An unstable receiver is read once by the caller through
    /// <see cref="OperandAccess"/> hoisting.
    /// </summary>
    public static ParenthesizedExpressionSyntax GetValueOrDefault(
        OperandAccess receiver, ITypeSymbol underlying)
    {
        var valueType = UnderlyingTypeSyntax(underlying);
        var conditional = Guard(ConditionalExpression(
            receiver.HasValueExpression(),
            receiver.ValueExpression(valueType),
            DefaultExpression(valueType)));
        return ParenthesizedExpression(conditional);
    }

    /// <summary>
    /// NL-6 one-argument GOV (decision 10):
    /// <c>x.GetValueOrDefault(d) → (x.HasValue ? x.Value : __cs2j_n_d)</c>;
    /// the default argument (and an unstable receiver) are evaluated once.
    /// </summary>
    public static ParenthesizedExpressionSyntax GetValueOrDefault(
        OperandAccess receiver, OperandAccess defaultValue, ITypeSymbol underlying)
    {
        var valueType = UnderlyingTypeSyntax(underlying);
        var conditional = Guard(ConditionalExpression(
            receiver.HasValueExpression(),
            receiver.ValueExpression(valueType),
            defaultValue.ValueExpression(valueType)));
        return ParenthesizedExpression(conditional);
    }

    // ── NL-7/NL-8 conversions (decision 13) ──

    /// <summary>
    /// NL-7 unwrap ② <c>(T)x</c> with <c>x: T?</c> → <c>x.Value</c>, and the
    /// ④ <c>S? → T</c> unwrap-and-convert family → <c>(T)x.Value</c>: a null
    /// operand reaches <c>.Value</c> and throws
    /// <see cref="InvalidOperationException"/>, exactly like the source cast.
    /// </summary>
    public static ExpressionSyntax UnwrapValue(
        OperandAccess source, ITypeSymbol target, ITypeSymbol? sourceUnderlying)
    {
        var valueExpression = source.ValueExpression(UnderlyingTypeSyntax(sourceUnderlying ?? target));
        if (SymbolEqualityComparer.Default.Equals(sourceUnderlying, target))
        {
            return valueExpression;
        }

        return ParenthesizedExpression(
            CastExpression(UnderlyingTypeSyntax(target), valueExpression));
    }

    /// <summary>
    /// NL-8 lifted conversion ③ <c>(T?)x</c> with <c>x: S?</c> (S≠T):
    /// <c>(x.HasValue ? (T?)(T)x.Value : (T?)null)</c>. A non-nullable source
    /// <c>S → T?</c> binds the same lifted conversion family but can never be
    /// null, so it renders <c>(T?)(T)x</c> without a guard.
    /// </summary>
    public static ParenthesizedExpressionSyntax LiftedConversionGuard(
        OperandAccess source,
        ITypeSymbol sourceUnderlying,
        ITypeSymbol targetUnderlying,
        bool sourceNullable)
    {
        var nullableType = NullableTypeSyntax(targetUnderlying);
        var targetValueType = UnderlyingTypeSyntax(targetUnderlying);

        var sourceValue = source.ValueExpression(UnderlyingTypeSyntax(sourceUnderlying));
        var live = SymbolEqualityComparer.Default.Equals(sourceUnderlying, targetUnderlying)
            ? sourceValue
            : CastExpression(targetValueType, ParenthesizedExpression(sourceValue));

        // A non-nullable position (HasValue contribution is the literal true)
        // never takes the null branch: drop the guard and keep the wrap only.
        if (!sourceNullable || source.HasValueExpression().IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return ParenthesizedExpression(
                CastNullable(nullableType, ParenthesizedExpression(live)));
        }

        var conditional = Guard(ConditionalExpression(
            source.HasValueExpression(),
            CastNullable(nullableType, ParenthesizedExpression(live)),
            CastNull(nullableType)));
        return ParenthesizedExpression(conditional);
    }

    // ── NL-9 nullable-enum backstop (decision 12) ──

    /// <summary>
    /// NL-9 lifted enum bitwise binary backstop (<c>&amp; | ^</c>), the same
    /// cast shape E04 emits for stable operands:
    /// <c>(a.HasValue &amp;&amp; b.HasValue ? (E?)(E)((U)a.Value op (U)b.Value) : (E?)null)</c>.
    /// Unstable operands are hoisted by the rewriter (instead of E04's
    /// warning-and-skip). Bitwise results of two U-width values always fit U,
    /// so unlike <see cref="EnumBackstopNot"/> no <c>unchecked</c> wrap is
    /// needed even for narrow underlying types (mirrors E04).
    /// </summary>
    public static ParenthesizedExpressionSyntax EnumBackstopBinary(
        SyntaxKind binaryKind,
        SyntaxKind operatorKind,
        OperandAccess left,
        OperandAccess right,
        INamedTypeSymbol enumType)
    {
        var underlyingSyntax = EnumUnderlyingSyntax(enumType);
        var nullableEnum = NullableTypeSyntax(enumType);

        var leftInner = CastExpression(
            underlyingSyntax, ParenthesizedExpression(left.ValueExpression(underlyingSyntax)));
        var rightInner = CastExpression(
            underlyingSyntax, ParenthesizedExpression(right.ValueExpression(underlyingSyntax)));
        var inner = BinaryExpression(binaryKind, leftInner, Token(operatorKind), rightInner);
        var enumCast = CastExpression(
            UnderlyingTypeSyntax(enumType), ParenthesizedExpression(inner));

        var conditional = Guard(ConditionalExpression(
            AllHaveValues(left, right),
            CastNullable(nullableEnum, ParenthesizedExpression(enumCast)),
            CastNull(nullableEnum)));
        return ParenthesizedExpression(conditional);
    }

    /// <summary>
    /// NL-9 lifted enum <c>~</c> backstop:
    /// <c>(x.HasValue ? (E?)(E)(~(U)x.Value) : (E?)null)</c>. Narrow
    /// underlying types (byte/sbyte/short/ushort) keep E04's
    /// <c>unchecked((U)(~(U)x.Value))</c> inner wrap because the promoted
    /// complement does not fit U; the NB-1 pass strips the unchecked shell
    /// outside checked scopes.
    /// </summary>
    public static ParenthesizedExpressionSyntax EnumBackstopNot(
        OperandAccess operand, INamedTypeSymbol enumType)
    {
        var underlying = enumType.EnumUnderlyingType!;
        var underlyingSyntax = UnderlyingTypeSyntax(underlying);

        var castedValue = CastExpression(
            underlyingSyntax, ParenthesizedExpression(operand.ValueExpression(underlyingSyntax)));
        var complement = PrefixUnaryExpression(
            SyntaxKind.BitwiseNotExpression,
            Token(SyntaxKind.TildeToken),
            ParenthesizedExpression(castedValue));
        ExpressionSyntax narrowed = IsNarrowEnumUnderlying(underlying.SpecialType)
            ? CheckedExpression(
                SyntaxKind.UncheckedExpression,
                CastExpression(underlyingSyntax, ParenthesizedExpression(complement)))
            : CastExpression(underlyingSyntax, ParenthesizedExpression(complement));

        var nullableEnum = NullableTypeSyntax(enumType);
        var enumCast = CastExpression(
            UnderlyingTypeSyntax(enumType), ParenthesizedExpression(narrowed));
        var conditional = Guard(ConditionalExpression(
            operand.HasValueExpression(),
            CastNullable(nullableEnum, ParenthesizedExpression(enumCast)),
            CastNull(nullableEnum)));
        return ParenthesizedExpression(conditional);
    }

    private static TypeSyntax EnumUnderlyingSyntax(INamedTypeSymbol enumType) =>
        UnderlyingTypeSyntax(enumType.EnumUnderlyingType!);

    // ── NL-10 lifted increment/decrement (decision 15) ──

    /// <summary>
    /// NL-10 numeric lifted increment RHS guard (decision 15):
    /// <c>s.HasValue ? (T?)(s.Value + 1) : (T?)null</c> (decrement uses
    /// <c>- 1</c>). <paramref name="source"/> is the once-read value carrier:
    /// the postfix old-value temp <c>__cs2j_n_oN</c> or the prefix target.
    /// In a checked context the inner <c>+1</c> is swept to a C15 helper.
    /// </summary>
    public static ParenthesizedExpressionSyntax IncrementGuard(
        OperandAccess source, bool isDecrement, ITypeSymbol underlying)
    {
        var nullableType = NullableTypeSyntax(underlying);
        var valueType = UnderlyingTypeSyntax(underlying);
        var inner = BinaryExpression(
            isDecrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
            source.ValueExpression(valueType),
            Token(isDecrement ? SyntaxKind.MinusToken : SyntaxKind.PlusToken),
            OneLiteral());
        var conditional = Guard(ConditionalExpression(
            source.HasValueExpression(),
            CastNullable(nullableType, ParenthesizedExpression(inner)),
            CastNull(nullableType)));
        return ParenthesizedExpression(conditional);
    }

    /// <summary>
    /// NL-10 enum-domain lifted increment RHS guard (decision 15, same cast
    /// family as <see cref="EnumBackstopBinary"/>):
    /// <c>s.HasValue ? (E?)(E)((U)s.Value + 1) : (E?)null</c>. The
    /// <c>(U)</c> cast is required because enum + integer is not an
    /// operator C# binds; the integer literal promotes to <c>U</c>.
    /// </summary>
    public static ParenthesizedExpressionSyntax EnumIncrementGuard(
        OperandAccess source, bool isDecrement, INamedTypeSymbol enumType)
    {
        var underlyingSyntax = EnumUnderlyingSyntax(enumType);
        var nullableEnum = NullableTypeSyntax(enumType);

        var castedValue = CastExpression(
            underlyingSyntax, ParenthesizedExpression(source.ValueExpression(underlyingSyntax)));
        var inner = BinaryExpression(
            isDecrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
            castedValue,
            Token(isDecrement ? SyntaxKind.MinusToken : SyntaxKind.PlusToken),
            OneLiteral());
        var enumCast = CastExpression(
            UnderlyingTypeSyntax(enumType), ParenthesizedExpression(inner));

        var conditional = Guard(ConditionalExpression(
            source.HasValueExpression(),
            CastNullable(nullableEnum, ParenthesizedExpression(enumCast)),
            CastNull(nullableEnum)));
        return ParenthesizedExpression(conditional);
    }

    private static LiteralExpressionSyntax OneLiteral() =>
        LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(1));

    private static bool IsNarrowEnumUnderlying(SpecialType specialType) =>
        specialType is SpecialType.System_Byte
            or SpecialType.System_SByte
            or SpecialType.System_Int16
            or SpecialType.System_UInt16;

    // ── shared rendering primitives ──

    private static ExpressionSyntax AllHaveValues(OperandAccess left, OperandAccess right)
    {
        var leftHasValue = left.HasValueExpression();
        var rightHasValue = right.HasValueExpression();

        if (leftHasValue.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return rightHasValue;
        }

        if (rightHasValue.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            return leftHasValue;
        }

        return BinaryExpression(
            SyntaxKind.LogicalAndExpression,
            leftHasValue,
            Token(SyntaxKind.AmpersandAmpersandToken),
            rightHasValue);
    }

    private static CastExpressionSyntax CastNullable(TypeSyntax nullableType, ExpressionSyntax inner) =>
        CastExpression(nullableType, inner);

    private static CastExpressionSyntax CastNull(TypeSyntax nullableType) =>
        CastExpression(nullableType, LiteralExpression(SyntaxKind.NullLiteralExpression));

    /// <summary>Tag a synthesized guard conditional for the C15 seam (decision 18).</summary>
    private static ConditionalExpressionSyntax Guard(ConditionalExpressionSyntax conditional) =>
        conditional.WithAdditionalAnnotations(NullableLiftSyntaxFacts.GuardAnnotation);

    /// <summary>Non-nullable underlying type syntax (keyword or fully-qualified name).</summary>
    public static TypeSyntax UnderlyingTypeSyntax(ITypeSymbol underlying) =>
        PredefinedOrFullyQualified(underlying);

    /// <summary><c>T?</c> type syntax for the guard casts.</summary>
    public static TypeSyntax NullableTypeSyntax(ITypeSymbol underlying) =>
        SyntaxFactory.NullableType(PredefinedOrFullyQualified(underlying));

    private static TypeSyntax PredefinedOrFullyQualified(ITypeSymbol type)
    {
        var keyword = PredefinedKeyword(type.SpecialType);
        if (keyword != SyntaxKind.None)
        {
            return SpecialTypeSyntax(type.SpecialType);
        }

        return ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static SyntaxKind PredefinedKeyword(SpecialType specialType) => specialType switch
    {
        SpecialType.System_Boolean => SyntaxKind.BoolKeyword,
        SpecialType.System_Byte => SyntaxKind.ByteKeyword,
        SpecialType.System_SByte => SyntaxKind.SByteKeyword,
        SpecialType.System_Char => SyntaxKind.CharKeyword,
        SpecialType.System_Decimal => SyntaxKind.DecimalKeyword,
        SpecialType.System_Double => SyntaxKind.DoubleKeyword,
        SpecialType.System_Single => SyntaxKind.FloatKeyword,
        SpecialType.System_Int32 => SyntaxKind.IntKeyword,
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
        SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
        _ => SyntaxKind.None,
    };

    private static PredefinedTypeSyntax SpecialTypeSyntax(SpecialType specialType) =>
        PredefinedType(Token(PredefinedKeyword(specialType)));
}

/// <summary>
/// How one operand of a lifted site is read inside a rendered guard.
/// </summary>
internal readonly struct OperandAccess
{
    private readonly ExpressionSyntax? _expression;
    private readonly bool _isNullablePosition;

    private OperandAccess(ExpressionSyntax? expression, bool isNullablePosition, bool isNullConstant)
    {
        _expression = expression;
        _isNullablePosition = isNullablePosition;
        IsNullConstant = isNullConstant;
    }

    /// <summary>The operand evaluates to a null constant (<c>null</c>/<c>(T?)null</c>).</summary>
    public bool IsNullConstant { get; }

    /// <summary>Stable reference read directly, or an already-hoisted temp identifier.</summary>
    public static OperandAccess Direct(ExpressionSyntax expression, bool isNullablePosition) =>
        new(expression, isNullablePosition, isNullConstant: false);

    /// <summary>Semantically-null constant: HasValue reads false, value reads default(T).</summary>
    public static OperandAccess Null => new(expression: null, isNullablePosition: true, isNullConstant: true);

    /// <summary>
    /// The <c>.HasValue</c> contribution: <c>false</c> for a null constant,
    /// <c>true</c> for a non-nullable position, otherwise the operand's own
    /// HasValue member access.
    /// </summary>
    public ExpressionSyntax HasValueExpression()
    {
        if (IsNullConstant)
        {
            return LiteralExpression(SyntaxKind.FalseLiteralExpression);
        }

        if (!_isNullablePosition)
        {
            return LiteralExpression(SyntaxKind.TrueLiteralExpression);
        }

        return MemberAccess(_expression!, nameof(Nullable<int>.HasValue));
    }

    /// <summary>
    /// The value contribution: <c>default(T)</c> for a null constant (the
    /// value branch is dead but must still type-check), the bare expression
    /// for a non-nullable position, otherwise <c>.Value</c>.
    /// </summary>
    public ExpressionSyntax ValueExpression(TypeSyntax underlyingType)
    {
        if (IsNullConstant)
        {
            return DefaultExpression(underlyingType);
        }

        if (!_isNullablePosition)
        {
            return _expression!;
        }

        return MemberAccess(_expression!, nameof(Nullable<int>.Value));
    }

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax expression, string memberName) =>
        MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            expression,
            Token(SyntaxKind.DotToken),
            IdentifierName(memberName));
}
