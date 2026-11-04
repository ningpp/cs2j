using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-UNSAFE-BLOCK：unsafe 语句块（空块同样命中，Java 门禁对空块也硬失败）。
/// 对应门禁：StatementTransformer.TransformUnsafeStatement。
/// </summary>
internal sealed class UnsafeBlockResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-UNSAFE-BLOCK";
    internal const string Owner = "unsafe-ffm";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "unsafe statement block";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not UnsafeStatementSyntax unsafeStatement)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                unsafeStatement,
                "unsafe block must be lowered by a preprocessor before Java conversion");
        }
    }
}
