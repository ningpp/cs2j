using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-FIXED：fixed 语句（含空块）。
/// 对应门禁：StatementTransformer.TransformFixedStatement。
/// </summary>
internal sealed class FixedStatementResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-FIXED";
    internal const string Owner = "unsafe-ffm";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "fixed statement";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not FixedStatementSyntax fixedStatement)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                fixedStatement,
                "fixed statement must be lowered by a preprocessor before Java conversion");
        }
    }
}
