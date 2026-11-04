using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-STACKALLOC：显式与隐式 stackalloc 都必须由 stackalloc-lowering（在
/// unsafe-ffm 之后）降低。对应门禁：ObjectCreationTransformer.NewStackAlloc。
/// </summary>
internal sealed class StackAllocResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-STACKALLOC";
    internal const string Owner = "stackalloc-lowering";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "stackalloc array creation";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            var reason = node switch
            {
                StackAllocArrayCreationExpressionSyntax => "explicit stackalloc array creation",
                ImplicitStackAllocArrayCreationExpressionSyntax => "implicit stackalloc array creation",
                _ => null,
            };
            if (reason is null)
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                node,
                reason);
        }
    }
}
