using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-REF-LOCAL：局部声明类型为 RefTypeSyntax（例如 ref int r = ref x）。
/// 对应门禁：StatementTransformer.TransformLocalDeclaration。
/// </summary>
internal sealed class RefLocalResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-REF-LOCAL";
    internal const string Owner = "unsafe-ref-lowering";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "ref local declaration";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not LocalDeclarationStatementSyntax localDeclaration)
                continue;

            if (localDeclaration.Declaration.Type is not RefTypeSyntax)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                localDeclaration,
                "ref local must be lowered by a preprocessor before Java conversion");
        }
    }
}
