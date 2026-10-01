using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.DecimalOps;

/// <summary>
/// Roslyn syntax factories for the C34 output forms (design §2 decision table):
/// static <c>decimal.Add/Subtract/Multiply/Divide/Remainder/Negate/Compare</c>
/// calls, explicit <c>(decimal)</c> promotions and the E09
/// <c>Cs2jUnsigned.UnsignedMath.ToDecimalUInt32/64</c> calls.
/// </summary>
internal static class DecimalOpSyntaxFacts
{
    public const string RewriteAnnotationKind = "Cs2jDecimalOpsRewrite";
    public const string HoistAnnotationKind = "Cs2jDecimalOpsHoist";
    public const string ConstDowngradeAnnotationKind = "Cs2jDecimalOpsConstDowngrade";

    public const string ReceiverTempName = "__cs2j_dec_r";
    public const string ArgTempPrefix = "__cs2j_dec_a";
    public const string ValueTempName = "__cs2j_dec_t";

    public static readonly SyntaxAnnotation RewriteAnnotation = new(RewriteAnnotationKind, "true");
    public static readonly SyntaxAnnotation HoistAnnotation = new(HoistAnnotationKind, "true");

    // ── method-name mapping ──

    public static string? BinaryArithmeticMethod(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddExpression => "Add",
        SyntaxKind.SubtractExpression => "Subtract",
        SyntaxKind.MultiplyExpression => "Multiply",
        SyntaxKind.DivideExpression => "Divide",
        SyntaxKind.ModuloExpression => "Remainder",
        _ => null,
    };

    public static string? CompoundArithmeticMethod(SyntaxKind kind) => kind switch
    {
        SyntaxKind.AddAssignmentExpression => "Add",
        SyntaxKind.SubtractAssignmentExpression => "Subtract",
        SyntaxKind.MultiplyAssignmentExpression => "Multiply",
        SyntaxKind.DivideAssignmentExpression => "Divide",
        SyntaxKind.ModuloAssignmentExpression => "Remainder",
        _ => null,
    };

    // ── expression factories ──

    public static TypeSyntax DecimalType() =>
        PredefinedType(Token(SyntaxKind.DecimalKeyword));

    public static ExpressionSyntax DecimalStaticCall(string methodName, ExpressionSyntax arg1, ExpressionSyntax arg2)
    {
        ExpressionSyntax call = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                DecimalType(),
                IdentifierName(methodName)),
            ArgumentList(SeparatedList(new[]
            {
                Argument(arg1),
                Argument(arg2),
            })));

        return ParenthesizedExpression(call);
    }

    public static ExpressionSyntax DecimalNegateCall(ExpressionSyntax operand)
    {
        ExpressionSyntax call = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                DecimalType(),
                IdentifierName("Negate")),
            ArgumentList(SingletonSeparatedList(Argument(operand))));

        return ParenthesizedExpression(call);
    }

    /// <summary>
    /// <c>(decimal.Compare(a, b) OP 0)</c> for the six comparison directions.
    /// Never <c>.Equals</c> (scale-dependent), never instance <c>CompareTo</c>.
    /// </summary>
    public static ExpressionSyntax DecimalComparison(
        SyntaxKind comparisonKind,
        ExpressionSyntax left,
        ExpressionSyntax right)
    {
        ExpressionSyntax compare = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                DecimalType(),
                IdentifierName("Compare")),
            ArgumentList(SeparatedList(new[]
            {
                Argument(left),
                Argument(right),
            })));

        var opToken = comparisonKind switch
        {
            SyntaxKind.EqualsExpression => Token(SyntaxKind.EqualsEqualsToken),
            SyntaxKind.NotEqualsExpression => Token(SyntaxKind.ExclamationEqualsToken),
            SyntaxKind.LessThanExpression => Token(SyntaxKind.LessThanToken),
            SyntaxKind.LessThanOrEqualExpression => Token(SyntaxKind.LessThanEqualsToken),
            SyntaxKind.GreaterThanExpression => Token(SyntaxKind.GreaterThanToken),
            SyntaxKind.GreaterThanOrEqualExpression => Token(SyntaxKind.GreaterThanEqualsToken),
            _ => throw new InvalidOperationException($"not a comparison: {comparisonKind}"),
        };

        ExpressionSyntax comparison = BinaryExpression(
            comparisonKind,
            compare.WithoutTrivia(),
            opToken,
            LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(0)));

        return ParenthesizedExpression(comparison);
    }

    /// <summary>Wrap an operand in an explicit <c>(decimal)</c> cast.</summary>
    public static ExpressionSyntax CastToDecimal(ExpressionSyntax operand) =>
        CastExpression(DecimalType(), ParenthesizeIfNeeded(operand));

    public static ExpressionSyntax ToDecimalUInt32Call(ExpressionSyntax operand) =>
        UnsignedMathCall("ToDecimalUInt32", operand);

    public static ExpressionSyntax ToDecimalUInt64Call(ExpressionSyntax operand) =>
        UnsignedMathCall("ToDecimalUInt64", operand);

    private static ExpressionSyntax UnsignedMathCall(string method, ExpressionSyntax operand)
    {
        ExpressionSyntax call = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    global::Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("Cs2jUnsigned"),
                    global::Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName("UnsignedMath")),
                global::Microsoft.CodeAnalysis.CSharp.SyntaxFactory.IdentifierName(method)),
            ArgumentList(SingletonSeparatedList(Argument(operand))));

        return ParenthesizedExpression(call);
    }

    /// <summary>
    /// Promote a non-decimal operand per DC-9. Real uint/ulong primitives arrive
    /// as E09 does not run in checked regions; after the C15 pass the tree is in
    /// unchecked context, so plain <c>(int)</c>/<c>(long)</c> reinterpretation
    /// casts are emitted (no checked/unchecked keyword nodes survive).
    /// </summary>
    public static ExpressionSyntax PromoteOperand(
        DecimalTypeFacts.OperandDomain domain,
        ExpressionSyntax operand) => domain switch
    {
        DecimalTypeFacts.OperandDomain.Decimal => operand,
        DecimalTypeFacts.OperandDomain.LiteralInteger
            or DecimalTypeFacts.OperandDomain.SignedOrNarrowInt
            => CastToDecimal(operand),
        DecimalTypeFacts.OperandDomain.NativeUInt32
            => ToDecimalUInt32Call(CastExpression(
                PredefinedType(Token(SyntaxKind.IntKeyword)),
                ParenthesizeIfNeeded(operand))),
        DecimalTypeFacts.OperandDomain.NativeUInt64
            => ToDecimalUInt64Call(CastExpression(
                PredefinedType(Token(SyntaxKind.LongKeyword)),
                ParenthesizeIfNeeded(operand))),
        // E09 wrappers (dotnet.System.UInt/ULong and the defensive
        // Cs2jUnsigned.* fingerprints) expose the raw bits through explicit
        // int/long conversion operators; reinterpret them exactly like the
        // native uint/ulong primitives (a user-defined explicit conversion —
        // no unchecked keyword node is introduced).
        DecimalTypeFacts.OperandDomain.WrappedUInt32
            => ToDecimalUInt32Call(CastExpression(
                PredefinedType(Token(SyntaxKind.IntKeyword)),
                ParenthesizeIfNeeded(operand))),
        DecimalTypeFacts.OperandDomain.WrappedUInt64
            => ToDecimalUInt64Call(CastExpression(
                PredefinedType(Token(SyntaxKind.LongKeyword)),
                ParenthesizeIfNeeded(operand))),
        _ => throw new InvalidOperationException($"cannot promote operand domain {domain}"),
    };

    /// <summary>
    /// Parenthesize a promoted/rewritten operand when it is not a leaf-like
    /// expression, preserving evaluation order exactly.
    /// </summary>
    public static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression)
    {
        if (expression is ParenthesizedExpressionSyntax
            or LiteralExpressionSyntax
            or IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or CastExpressionSyntax)
        {
            return expression;
        }

        return ParenthesizedExpression(expression);
    }

    public static bool IsComparisonKind(SyntaxKind kind) => kind is
        SyntaxKind.EqualsExpression
        or SyntaxKind.NotEqualsExpression
        or SyntaxKind.LessThanExpression
        or SyntaxKind.LessThanOrEqualExpression
        or SyntaxKind.GreaterThanExpression
        or SyntaxKind.GreaterThanOrEqualExpression;
}
