using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-POINTER-OP：前缀 &amp;（取址）与前缀 *（指针解引用）在 Java 中没有对应物。
/// 对应门禁：UnaryExpressionTransformer。
/// </summary>
internal sealed class PointerOperatorResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-POINTER-OP";
    internal const string Owner = "unsafe-ffm";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "address-of '&' / pointer indirection '*'";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not PrefixUnaryExpressionSyntax prefix)
                continue;

            var reason = prefix.Kind() switch
            {
                SyntaxKind.AddressOfExpression => "address-of '&'",
                SyntaxKind.PointerIndirectionExpression => "pointer indirection '*'",
                _ => null,
            };
            if (reason is null)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                prefix,
                reason);
        }
    }
}
