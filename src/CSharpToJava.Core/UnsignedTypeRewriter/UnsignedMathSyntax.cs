using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.UnsignedTypeRewriter;

/// <summary>
/// 生成 Cs2jUnsigned.UnsignedMath 全限定静态调用所需的语法节点。
/// </summary>
internal static class UnsignedMathSyntax
{
    public static string Suffix(UnsignedDomain domain) => domain switch
    {
        UnsignedDomain.Int32 => "Int32",
        UnsignedDomain.Int64 => "Int64",
        UnsignedDomain.UInt32 => "UInt32",
        UnsignedDomain.UInt64 => "UInt64",
        _ => string.Empty,
    };

    public static InvocationExpressionSyntax StaticCall(
        string method, params ExpressionSyntax[] arguments)
    {
        var memberAccess = SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("Cs2jUnsigned"),
                SyntaxFactory.IdentifierName("UnsignedMath")),
            SyntaxFactory.IdentifierName(method));

        return SyntaxFactory.InvocationExpression(
            memberAccess,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                    arguments.Select(SyntaxFactory.Argument))));
    }

    public static CheckedExpressionSyntax UncheckedCast(
        SyntaxKind typeKeyword, ExpressionSyntax operand)
    {
        var cast = SyntaxFactory.CastExpression(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(typeKeyword)),
            EnsureCastOperand(operand));
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, cast);
    }

    /// <summary>
    /// unchecked((nuint)x)。nuint 是上下文关键字(Roslyn 无 NUIntKeyword),
    /// 只能按类型名解析。
    /// </summary>
    public static CheckedExpressionSyntax UncheckedNUIntCast(ExpressionSyntax operand)
    {
        var cast = SyntaxFactory.CastExpression(
            SyntaxFactory.ParseTypeName("nuint"),
            EnsureCastOperand(operand));
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, cast);
    }

    private static ExpressionSyntax EnsureCastOperand(ExpressionSyntax operand) => operand switch
    {
        IdentifierNameSyntax
            or MemberAccessExpressionSyntax
            or InvocationExpressionSyntax
            or ElementAccessExpressionSyntax
            or LiteralExpressionSyntax
            or ParenthesizedExpressionSyntax
            or CastExpressionSyntax
            or CheckedExpressionSyntax
            or ThisExpressionSyntax
            or BaseExpressionSyntax => operand,
        _ => SyntaxFactory.ParenthesizedExpression(operand),
    };

    /// <summary>
    /// 按目标域归一化操作数：uint→int/long、ulong→long 显式插入转换。
    /// byte/ushort→int 的提升两侧一致，直接传参。
    /// </summary>
    public static ExpressionSyntax Normalize(
        ExpressionSyntax operand,
        UnsignedDomain domain,
        SpecialType special)
    {
        return domain switch
        {
            UnsignedDomain.Int64 when special == SpecialType.System_UInt32 =>
                StaticCall("ToInt64UInt32", UncheckedCast(SyntaxKind.IntKeyword, operand)),
            UnsignedDomain.UInt32 when special == SpecialType.System_UInt32 =>
                UncheckedCast(SyntaxKind.IntKeyword, operand),
            UnsignedDomain.UInt64 when special == SpecialType.System_UInt64 =>
                UncheckedCast(SyntaxKind.LongKeyword, operand),
            UnsignedDomain.UInt64 when special == SpecialType.System_UInt32 =>
                StaticCall("ToInt64UInt32", UncheckedCast(SyntaxKind.IntKeyword, operand)),
            // nuint 参数即 Java long 的擦除形态,显式转 long 以通过 C# 编译。
            UnsignedDomain.UInt64 when special == SpecialType.System_UIntPtr =>
                UncheckedCast(SyntaxKind.LongKeyword, operand),
            _ => operand,
        };
    }

    /// <summary>
    /// 复合赋值/自增自减写回目标类型所需的收尾转换。
    /// </summary>
    public static ExpressionSyntax WrapTarget(
        SpecialType target, ExpressionSyntax value) => target switch
    {
        SpecialType.System_Byte => StaticCall("ToByteInt32", value),
        SpecialType.System_UInt16 => StaticCall("ToUInt16Int32", value),
        SpecialType.System_UInt32 => UncheckedCast(SyntaxKind.UIntKeyword, value),
        SpecialType.System_UInt64 => UncheckedCast(SyntaxKind.ULongKeyword, value),
        _ => value,
    };

    /// <summary>
    /// uint/ulong → 浮点/decimal 的无符号转换(C# 里多为隐式,Java 擦除后直接
    /// widening 会得到负值,必须走 helper)。
    /// </summary>
    public static ExpressionSyntax ToFloating(
        SpecialType source, SpecialType target, ExpressionSyntax operand)
    {
        var method = (source, target) switch
        {
            (SpecialType.System_UInt32, SpecialType.System_Double) => "ToDoubleUInt32",
            (SpecialType.System_UInt32, SpecialType.System_Single) => "ToSingleUInt32",
            (SpecialType.System_UInt32, SpecialType.System_Decimal) => "ToDecimalUInt32",
            (SpecialType.System_UInt64, SpecialType.System_Double) => "ToDoubleUInt64",
            (SpecialType.System_UInt64, SpecialType.System_Single) => "ToSingleUInt64",
            _ => "ToDecimalUInt64",
        };
        return StaticCall(method, NormalizeUnsigned(source, operand));
    }

    /// <summary>
    /// uint → long/ulong 的隐式零扩展(C# 隐式,Java int→long 符号扩展会出错)。
    /// </summary>
    public static ExpressionSyntax ToWide(
        SpecialType source, SpecialType target, ExpressionSyntax operand)
        => target == SpecialType.System_Int64
            ? StaticCall("ToInt64UInt32", NormalizeUnsigned(source, operand))
            : StaticCall("ToUInt64UInt32", NormalizeUnsigned(source, operand));

    private static ExpressionSyntax NormalizeUnsigned(SpecialType source, ExpressionSyntax operand)
        => source == SpecialType.System_UInt64
            ? UncheckedCast(SyntaxKind.LongKeyword, operand)
            : UncheckedCast(SyntaxKind.IntKeyword, operand);
}
