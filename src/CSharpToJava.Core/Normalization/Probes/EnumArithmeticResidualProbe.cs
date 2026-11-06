using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-ENUM-ARITH：Java 普通 enum 只允许同类 enum 的 == / !=。
/// 其余任何带 enum 操作数的二元运算（算术、移位、位运算、关系比较、
/// enum 与数值混合）都会被 Java 门禁硬失败。
/// 对应门禁：BinaryExpressionTransformer.EnsureDirectBinaryOperands。
/// </summary>
internal sealed class EnumArithmeticResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-ENUM-ARITH";
    internal const string Owner = "enum-declaration";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "enum operand in binary operator";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not BinaryExpressionSyntax binary)
                continue;

            var leftEnum = EnumTypeOrNullable(model.GetTypeInfo(binary.Left).Type);
            var rightEnum = EnumTypeOrNullable(model.GetTypeInfo(binary.Right).Type);
            if (leftEnum == null && rightEnum == null)
                continue;

            if (binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
            {
                // 可空 enum 与 null 字面量是 HasValue 判空（Java 引用判空直译），
                // 枚举 pass 有意保留，不构成 enum 操作数残留。
                if (binary.Left.IsKind(SyntaxKind.NullLiteralExpression)
                    || binary.Right.IsKind(SyntaxKind.NullLiteralExpression))
                {
                    continue;
                }

                // 同类 enum 的 == / != 在 Java 中合法（引用相等）；可空 <c>E?</c>
                // 与 <c>E</c> 的提升相等语义相同（null 与非 null 常量比较分别为
                // false / true），enum pass 归一化后的产物即此形态。
                if (leftEnum != null && rightEnum != null
                    && SymbolEqualityComparer.Default.Equals(leftEnum, rightEnum))
                {
                    continue;
                }
            }

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                binary,
                $"enum operand in binary operator '{binary.OperatorToken.Text}'");
        }
    }

    /// <summary>
    /// Returns the enum type of <paramref name="type"/>, unwrapping
    /// <c>Nullable&lt;E&gt;</c>; null for non-enum types.
    /// </summary>
    private static INamedTypeSymbol? EnumTypeOrNullable(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return null;

        if (named.TypeKind == TypeKind.Enum)
            return named;

        if (named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T
            && named.TypeArguments.Length == 1
            && named.TypeArguments[0] is INamedTypeSymbol inner
            && inner.TypeKind == TypeKind.Enum)
        {
            return inner;
        }

        return null;
    }
}
