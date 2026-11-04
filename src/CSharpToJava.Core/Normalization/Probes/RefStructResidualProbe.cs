using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-REF-STRUCT：声明处带 ref 修饰符的 struct（含 readonly ref struct）
/// 必须由 ref-struct-lowering 预处理器降低。只扫声明，不扫使用点。
/// 对应门禁：StructTransformer.TransformCore。
/// </summary>
internal sealed class RefStructResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-REF-STRUCT";
    internal const string Owner = "ref-struct-lowering";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "ref struct declaration";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not StructDeclarationSyntax structDeclaration)
                continue;

            if (!structDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                structDeclaration,
                $"ref struct '{structDeclaration.Identifier.Text}' declaration");
        }
    }
}
