using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NB = CSharpToJava.Core.NumericBoundary.NumericBoundarySyntaxFacts;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace CSharpToJava.Core.NumericBoundary.Rules.IncrementTargets;

/// <summary>
/// How a hoisted target reaches its receiver: a generated temporary, a
/// syntactically trivial receiver (this/base/bare identifier) or a static
/// type-name receiver rendered from the bound symbol.
/// </summary>
internal abstract class ReceiverRef
{
    public sealed class Temp : ReceiverRef
    {
        public required string Name { get; init; }
    }

    public sealed class This : ReceiverRef;

    public sealed class Base : ReceiverRef;

    public sealed class Identifier : ReceiverRef
    {
        public required string Name { get; init; }
    }

    /// <summary>Static member: receiver is a type name (e.g. <c>SomeType.StaticProp</c>).</summary>
    public sealed class StaticType : ReceiverRef
    {
        public required string TypeDisplay { get; init; }
    }

    public static ReceiverRef TempNamed(string name) => new Temp { Name = name };
}

/// <summary>
/// One rebuilt indexer argument / array index: a generated temporary plus the
/// original name-colon and ref/out kind (relevant for indexers).
/// </summary>
internal readonly record struct IndexRef(
    string TempName,
    string? NameColon,
    SyntaxKind RefKind);

/// <summary>
/// Storage-location descriptor for one owned site: every <c>BuildAccess</c>
/// call produces FRESH syntax nodes, because the read and the write are two
/// distinct occurrences in the emitted read-modify-write sequence.
/// </summary>
internal abstract class TargetDescriptor
{
    public abstract ExpressionSyntax BuildAccess();

    public sealed class SimpleIdentifier : TargetDescriptor
    {
        public required string Name { get; init; }

        public override ExpressionSyntax BuildAccess() => IdentifierName(Name);
    }

    public sealed class SimpleMember : TargetDescriptor
    {
        public required string Name { get; init; }
        public required bool IsThis { get; init; }

        public override ExpressionSyntax BuildAccess() =>
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IsThis ? ThisExpression() : BaseExpression(),
                IdentifierName(Name));
    }

    public sealed class Member : TargetDescriptor
    {
        public required string Name { get; init; }
        public required ReceiverRef Receiver { get; init; }

        public override ExpressionSyntax BuildAccess() =>
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                BuildReceiver(Receiver),
                IdentifierName(Name));
    }

    public sealed class Indexer : TargetDescriptor
    {
        public required ReceiverRef Receiver { get; init; }
        public required IReadOnlyList<IndexRef> Arguments { get; init; }

        public override ExpressionSyntax BuildAccess() =>
            ElementAccessExpression(BuildReceiver(Receiver))
                .WithArgumentList(BracketedArgumentList(SeparatedList(
                    Arguments.Select(static a => Argument(
                        a.NameColon != null ? NameColon(IdentifierName(a.NameColon)) : null,
                        Token(a.RefKind),
                        IdentifierName(a.TempName))))));
    }

    public sealed class ArrayElement : TargetDescriptor
    {
        public required ReceiverRef Receiver { get; init; }
        public required IReadOnlyList<IndexRef> Indices { get; init; }

        public override ExpressionSyntax BuildAccess() =>
            ElementAccessExpression(BuildReceiver(Receiver))
                .WithArgumentList(BracketedArgumentList(SeparatedList(
                    Indices.Select(static i => Argument(IdentifierName(i.TempName))))));
    }

    protected static ExpressionSyntax BuildReceiver(ReceiverRef receiver) => receiver switch
    {
        ReceiverRef.Temp temp => IdentifierName(temp.Name),
        ReceiverRef.This => ThisExpression(),
        ReceiverRef.Base => BaseExpression(),
        ReceiverRef.Identifier id => IdentifierName(id.Name),
        ReceiverRef.StaticType type => ParseName(type.TypeDisplay),
        _ => throw new InvalidOperationException(),
    };
}

/// <summary>
/// Pure (model-independent) arithmetic-core rendering (design §C):
/// BuiltinNarrow cast cores, EnumCast cores and UnsignedDomain cores in both
/// unchecked (narrow casts / UnsignedMath calls) and checked regions (bare
/// arithmetic left for the C15 sweep to helperize).
/// </summary>
internal static class IncrementTargetExpansion
{
    public static ExpressionSyntax BuildCore(
        IncrementTargetClassification classification,
        ITypeSymbol? operandType,
        ExpressionSyntax oldValue,
        bool decrement,
        IncrementTargetStatistics stats)
    {
        var kind = decrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression;
        return classification.Core switch
        {
            ArithmeticCore.BuiltinNarrow => BuildNarrowCore(operandType, oldValue, kind, classification.IsChecked),
            ArithmeticCore.EnumCast => BuildEnumCore(classification, operandType, oldValue, kind),
            ArithmeticCore.UnsignedDomain => BuildUnsignedCore(operandType, oldValue, decrement, classification.IsChecked, stats),
            ArithmeticCore.Decimal => BuildDecimalCore(oldValue, kind),
            _ => throw new InvalidOperationException(
                $"core {classification.Core} is not rewritable by C17 M1/M2"),
        };
    }

    /// <summary>
    /// IT-7 decimal core (decision 8): bare <c>old + 1m</c> / <c>old - 1m</c>
    /// with no cast — checked context never affects decimal arithmetic. The
    /// literal is built with the decimal <see cref="Literal"/> overload so the
    /// token carries an actual decimal value and an <c>m</c> suffix; its bound
    /// SpecialType.System_Decimal is what the downstream C34 pass keys on to
    /// reduce this binary to <c>decimal.Add/Subtract</c>. No compat call is
    /// emitted here (the Java method surface is C34's, CS2J5334 Info).
    /// </summary>
    private static ExpressionSyntax BuildDecimalCore(ExpressionSyntax old, SyntaxKind binaryKind)
    {
        var literalOneM = LiteralExpression(
            SyntaxKind.NumericLiteralExpression,
            Literal(1m));
        return Binary(binaryKind, old, literalOneM);
    }

    private static ExpressionSyntax Binary(SyntaxKind kind, ExpressionSyntax left, ExpressionSyntax right)
        => BinaryExpression(kind, left, right);

    private static ExpressionSyntax BuildNarrowCore(
        ITypeSymbol? type, ExpressionSyntax old, SyntaxKind binaryKind, bool isChecked)
    {
        var binary = Binary(binaryKind, old, NB.LiteralOne());
        SyntaxKind? keyword = type?.SpecialType switch
        {
            SpecialType.System_Int32 or SpecialType.System_Int64 => null,
            SpecialType.System_SByte => SyntaxKind.SByteKeyword,
            SpecialType.System_Byte => SyntaxKind.ByteKeyword,
            SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
            SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
            SpecialType.System_Char => SyntaxKind.CharKeyword,
            _ => null,
        };

        // int/long domains need no write-back cast (and in checked regions the
        // bare binary is exactly what the C15 sweep converts to AddChecked*).
        if (keyword == null)
        {
            return binary;
        }

        // Explicit parens are mandatory: SyntaxFactory does not auto-parenthesize
        // a binary nested inside a cast (raw printing would yield '(byte)b + 1',
        // which reparses as '((byte)b) + 1' — an int, not a byte).
        return CastExpression(
            PredefinedType(Token(keyword.Value)),
            ParenthesizedExpression(binary));
    }

    private static ExpressionSyntax BuildEnumCore(
        IncrementTargetClassification classification,
        ITypeSymbol? type,
        ExpressionSyntax old,
        SyntaxKind binaryKind)
    {
        if (type is not INamedTypeSymbol enumType || enumType.TypeKind != TypeKind.Enum)
        {
            throw new InvalidOperationException("EnumCast core requires an enum operand type");
        }

        var underlying = enumType.EnumUnderlyingType;
        if (underlying == null)
        {
            throw new InvalidOperationException("enum operand has no underlying type");
        }

        var enumName = ParseName(enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

        if (!classification.IsChecked)
        {
            // (E)(U)((U)old ± 1): the middle U cast keeps arithmetic in the
            // underlying domain; byte/short narrow write-back truncates
            // naturally and long/ulong domains are never promoted.
            var uKeyword = UnderlyingKeyword(underlying.SpecialType);
            var arithmetic = ParenthesizedExpression(Binary(
                binaryKind,
                CastExpression(PredefinedType(Token(uKeyword)), old),
                NB.LiteralOne()));
            return CastExpression(
                enumName,
                CastExpression(PredefinedType(Token(uKeyword)), arithmetic));
        }

        // Checked: emit the same bare shape C15 produces for simple enum
        // targets — narrow-backed enums normalize through Int32, the others
        // keep their own domain; the outer (E) cast is never checked.
        var domainKeyword = CheckedEnumDomainKeyword(underlying.SpecialType);
        var checkedArithmetic = ParenthesizedExpression(Binary(
            binaryKind,
            CastExpression(PredefinedType(Token(domainKeyword)), old),
            NB.LiteralOne()));
        return CastExpression(enumName, checkedArithmetic);
    }

    private static ExpressionSyntax BuildUnsignedCore(
        ITypeSymbol? type,
        ExpressionSyntax old,
        bool decrement,
        bool isChecked,
        IncrementTargetStatistics stats)
    {
        var method = (decrement ? "Subtract" : "Add");
        switch (type?.SpecialType)
        {
            // byte/ushort residuals: promoted Int32 arithmetic + narrow
            // write-back cast, no support-library call.
            case SpecialType.System_Byte:
                return CastExpression(
                    PredefinedType(Token(SyntaxKind.ByteKeyword)),
                    ParenthesizedExpression(Binary(
                        decrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
                        old, NB.LiteralOne())));
            case SpecialType.System_UInt16:
                return CastExpression(
                    PredefinedType(Token(SyntaxKind.UShortKeyword)),
                    ParenthesizedExpression(Binary(
                        decrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
                        old, NB.LiteralOne())));
            case SpecialType.System_UInt32:
                if (isChecked)
                {
                    // Bare uint arithmetic: the C15 sweep emits AddCheckedUInt32.
                    return Binary(
                        decrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
                        old, NB.LiteralOne());
                }

                stats.UnsignedMathCallSites++;
                return CastExpression(
                    PredefinedType(Token(SyntaxKind.UIntKeyword)),
                    NB.HelperCall(
                        method + "UInt32",
                        CastExpression(PredefinedType(Token(SyntaxKind.IntKeyword)), old),
                        NB.LiteralOne()));
            case SpecialType.System_UInt64:
                if (isChecked)
                {
                    return Binary(
                        decrement ? SyntaxKind.SubtractExpression : SyntaxKind.AddExpression,
                        old, NB.LiteralOne());
                }

                stats.UnsignedMathCallSites++;
                return CastExpression(
                    PredefinedType(Token(SyntaxKind.ULongKeyword)),
                    NB.HelperCall(
                        method + "UInt64",
                        CastExpression(PredefinedType(Token(SyntaxKind.LongKeyword)), old),
                        NB.LiteralOne()));
            default:
                throw new InvalidOperationException(
                    $"UnsignedDomain core on unsupported special type {type?.SpecialType}");
        }
    }

    private static SyntaxKind UnderlyingKeyword(SpecialType special) => special switch
    {
        SpecialType.System_SByte => SyntaxKind.SByteKeyword,
        SpecialType.System_Byte => SyntaxKind.ByteKeyword,
        SpecialType.System_Int16 => SyntaxKind.ShortKeyword,
        SpecialType.System_UInt16 => SyntaxKind.UShortKeyword,
        SpecialType.System_Int32 => SyntaxKind.IntKeyword,
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        SpecialType.System_Char => SyntaxKind.CharKeyword,
        _ => throw new InvalidOperationException($"non-integral enum underlying type {special}"),
    };

    /// <summary>C15 NB-8 matrix: narrow-backed enums normalize through Int32.</summary>
    private static SyntaxKind CheckedEnumDomainKeyword(SpecialType underlying) => underlying switch
    {
        SpecialType.System_UInt32 => SyntaxKind.UIntKeyword,
        SpecialType.System_Int64 => SyntaxKind.LongKeyword,
        SpecialType.System_UInt64 => SyntaxKind.ULongKeyword,
        _ => SyntaxKind.IntKeyword,
    };
}
