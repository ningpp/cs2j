using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.NumericBoundary;

/// <summary>
/// Static facts for numeric-boundary classification and code generation.
/// Literal materialization is centralized here on purpose (design risk 6): every
/// target-type-specific suffix/sign/format decision has one implementation and
/// one test surface.
/// </summary>
internal static class NumericBoundarySyntaxFacts
{
    public const string UnwrapBlockAnnotationKind = "Cs2jNumericBoundaryUnwrapBlock";
    public const string RewriteAnnotationKind = "Cs2jNumericBoundaryRewrite";

    public static readonly SyntaxAnnotation UnwrapBlockAnnotation = new(UnwrapBlockAnnotationKind);
    public static readonly SyntaxAnnotation RewriteAnnotation = new(RewriteAnnotationKind);

    /// <summary>
    /// Integer domains C# arithmetic is performed in. Includes <c>char</c> and the
    /// narrow operand types (their arithmetic is promoted, but conversion sites
    /// still target them).
    /// </summary>
    public static bool IsIntegralSpecialType(SpecialType specialType) => specialType is
        SpecialType.System_SByte
        or SpecialType.System_Byte
        or SpecialType.System_Int16
        or SpecialType.System_UInt16
        or SpecialType.System_Int32
        or SpecialType.System_UInt32
        or SpecialType.System_Int64
        or SpecialType.System_UInt64
        or SpecialType.System_Char;

    public static bool IsIntegralType(ITypeSymbol? type)
        => type is not null && type.TypeKind == TypeKind.Structure && IsIntegralSpecialType(type.SpecialType);

    /// <summary>decimal/float/double: checked/unchecked has no effect on these operations.</summary>
    public static bool IsFloatingPointOrDecimalType(ITypeSymbol? type)
        => type?.SpecialType is SpecialType.System_Decimal
            or SpecialType.System_Single
            or SpecialType.System_Double;

    public static bool IsEnumType(ITypeSymbol? type) => type?.TypeKind == TypeKind.Enum;

    /// <summary>
    /// After stripping <c>unchecked(e)</c>/<c>checked(e)</c> the inner expression
    /// replaces the shell at a primary-expression precedence position. Low
    /// precedence inner forms must be parenthesized to preserve semantics
    /// (e.g. <c>unchecked(a + b) * 2</c>).
    /// </summary>
    public static bool NeedsParenthesesAfterShellStrip(ExpressionSyntax expression)
    {
        switch (expression)
        {
            // Unary/postfix and atom forms bind tight enough.
            case LiteralExpressionSyntax:
            case IdentifierNameSyntax:
            case MemberBindingExpressionSyntax:
            case MemberAccessExpressionSyntax:
            case ConditionalAccessExpressionSyntax:
            case InvocationExpressionSyntax:
            case ElementAccessExpressionSyntax:
            case ObjectCreationExpressionSyntax:
            case ImplicitObjectCreationExpressionSyntax:
            case StackAllocArrayCreationExpressionSyntax:
            case CastExpressionSyntax:
            case ParenthesizedExpressionSyntax:
            case ThisExpressionSyntax:
            case BaseExpressionSyntax:
            case InterpolatedStringExpressionSyntax:
            case DefaultExpressionSyntax:
            case TypeOfExpressionSyntax:
            case SizeOfExpressionSyntax:
            case AnonymousMethodExpressionSyntax:
            case SimpleLambdaExpressionSyntax:
            case ParenthesizedLambdaExpressionSyntax:
            case PrefixUnaryExpressionSyntax:
            case PostfixUnaryExpressionSyntax:
            case ArrayCreationExpressionSyntax:
            case ImplicitArrayCreationExpressionSyntax:
                return false;
            default:
                // Binary, assignments, ternary, switch/is-pattern, throw, await? stay safe.
                return true;
        }
    }

    public static ExpressionSyntax ParenthesizeIfNeeded(ExpressionSyntax expression)
        => NeedsParenthesesAfterShellStrip(expression)
            ? SyntaxFactory.ParenthesizedExpression(expression)
            : expression;

    /// <summary>
    /// Build a legal C# literal (with one same-domain cast when the target is a
    /// narrow/enum type) holding <paramref name="constantValue"/> as returned by
    /// <c>SemanticModel.GetConstantValue</c>/<c>IOperation.ConstantValue</c>.
    /// Returns false for values that cannot be rendered as a simple literal
    /// (non-finite floats, strings, …).
    /// </summary>
    public static bool TryMaterializeLiteral(
        ITypeSymbol targetType,
        object? constantValue,
        out ExpressionSyntax literal)
    {
        literal = null!;

        if (constantValue == null)
        {
            return false;
        }

        if (targetType.TypeKind == TypeKind.Enum)
        {
            var underlying = ((INamedTypeSymbol)targetType).EnumUnderlyingType;
            if (underlying == null || !TryRenderIntegral(underlying, constantValue, castPrefix: null, out var text))
            {
                return false;
            }

            literal = SyntaxFactory.ParseExpression(
                "(" + targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")" + text);
            return true;
        }

        if (IsFloatingPointOrDecimalType(targetType))
        {
            if (!TryRenderReal(targetType.SpecialType, constantValue, out var text))
            {
                return false;
            }

            literal = SyntaxFactory.ParseExpression(text);
            return true;
        }

        if (!IsIntegralType(targetType))
        {
            return false;
        }

        if (!TryRenderIntegral(targetType, constantValue, castPrefix: null, out var integralText))
        {
            return false;
        }

        literal = SyntaxFactory.ParseExpression(integralText);
        return true;
    }

    /// <summary>
    /// Render the integral text for <paramref name="value"/> in the target domain,
    /// truncating/reinterpreting exactly like an unchecked numeric conversion.
    /// Narrow domains and enums get a leading cast (callers pass the cast prefix;
    /// null renders just the literal for int/uint/long/ulong domains).
    /// </summary>
    private static bool TryRenderIntegral(
        ITypeSymbol targetType,
        object value,
        string? castPrefix,
        out string text)
    {
        text = null!;
        if (!TryGetBits(value, out var bits))
        {
            return false;
        }

        string number;
        string? castType = castPrefix;
        switch (targetType.SpecialType)
        {
            case SpecialType.System_SByte:
                castType ??= "sbyte";
                number = ToSigned(bits, 8).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_Byte:
                castType ??= "byte";
                number = (bits & 0xFFUL).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_Int16:
                castType ??= "short";
                number = ToSigned(bits, 16).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_UInt16:
                castType ??= "ushort";
                number = (bits & 0xFFFFUL).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_Char:
                castType ??= "char";
                number = (bits & 0xFFFFUL).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_Int32:
                number = ToSigned(bits, 32).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_UInt32:
                number = (bits & 0xFFFFFFFFUL).ToString(CultureInfo.InvariantCulture);
                break;
            case SpecialType.System_Int64:
            {
                var signed = unchecked((long)bits);
                number = signed.ToString(CultureInfo.InvariantCulture);
                if (signed is < int.MinValue or > int.MaxValue)
                {
                    number += "L";
                }

                break;
            }
            case SpecialType.System_UInt64:
                number = bits.ToString(CultureInfo.InvariantCulture);
                if (bits > long.MaxValue)
                {
                    number += "UL";
                }

                break;
            default:
                if (targetType.TypeKind != TypeKind.Enum)
                {
                    return false;
                }

                var underlying = ((INamedTypeSymbol)targetType).EnumUnderlyingType;
                if (underlying == null)
                {
                    return false;
                }

                return TryRenderIntegral(
                    underlying, value,
                    castPrefix ?? targetType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    out text);
        }

        text = castType != null ? "(" + castType + ")" + number : number;
        return true;
    }

    private static long ToSigned(ulong bits, int width)
    {
        ulong mask = width == 64 ? 0UL : (1UL << width) - 1;
        ulong truncated = width == 64 ? bits : bits & mask;
        ulong signBit = 1UL << (width - 1);
        if ((truncated & signBit) != 0)
        {
            // truncated - 2^width, evaluated in unsigned wraparound then reinterpreted.
            return unchecked((long)(truncated - (signBit << 1)));
        }

        return (long)truncated;
    }

    private static bool TryGetBits(object value, out ulong bits)
    {
        switch (value)
        {
            case ulong u:
                bits = u;
                return true;
            case long l:
                bits = unchecked((ulong)l);
                return true;
            case sbyte or byte or short or ushort or int or uint or char:
                bits = unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return true;
            default:
                bits = 0;
                return false;
        }
    }

    private static bool TryRenderReal(SpecialType specialType, object value, out string text)
    {
        text = null!;
        switch (specialType, value)
        {
            case (SpecialType.System_Single, float f):
                if (float.IsNaN(f) || float.IsInfinity(f))
                {
                    return false;
                }

                text = f.ToString("R", CultureInfo.InvariantCulture) + "F";
                return true;
            case (SpecialType.System_Double, double d):
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    return false;
                }

                text = d.ToString("R", CultureInfo.InvariantCulture);
                return true;
            case (SpecialType.System_Decimal, decimal m):
                text = m.ToString(CultureInfo.InvariantCulture) + "M";
                return true;
            default:
                return false;
        }
    }
}
