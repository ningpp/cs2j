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
            operand);
        return SyntaxFactory.CheckedExpression(
            SyntaxKind.UncheckedExpression, cast);
    }

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
}
