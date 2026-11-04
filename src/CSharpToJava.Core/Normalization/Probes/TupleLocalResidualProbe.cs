using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-TUPLE-LOCAL：局部声明类型为 TupleTypeSyntax（例如 (int, int) v = default）。
/// 这是 Java 门禁的精确形态；返回值/参数/字段中的元组由 tuple pass 的其他规则处理。
/// 对应门禁：StatementTransformer.TransformLocalDeclaration。
/// </summary>
internal sealed class TupleLocalResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-TUPLE-LOCAL";
    internal const string Owner = "tuple";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "tuple-typed local declaration";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not LocalDeclarationStatementSyntax localDeclaration)
                continue;

            if (localDeclaration.Declaration.Type is not TupleTypeSyntax)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                localDeclaration,
                "deconstruction local declaration must be lowered by a preprocessor before Java conversion");
        }
    }
}
