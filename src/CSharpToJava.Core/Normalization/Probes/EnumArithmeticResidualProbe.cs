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

            var leftIsEnum = model.GetTypeInfo(binary.Left).Type?.TypeKind == TypeKind.Enum;
            var rightIsEnum = model.GetTypeInfo(binary.Right).Type?.TypeKind == TypeKind.Enum;
            if (!leftIsEnum && !rightIsEnum)
                continue;

            // 同类 enum 的 == / != 在 Java 中合法，不需要预处理器降低。
            if (binary.Kind() is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
                && leftIsEnum && rightIsEnum)
            {
                continue;
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
}
