using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-ASYNC-LAMBDA：带 async 修饰符的 lambda（括号式与简单式都覆盖）
/// 必须由 async-normalize 预处理器提前提升。
/// 对应门禁：LambdaTransformer.TransformLambda。
/// </summary>
internal sealed class AsyncLambdaResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-ASYNC-LAMBDA";
    internal const string Owner = "async-normalize";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "async lambda";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not LambdaExpressionSyntax lambda)
                continue;

            if (!lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
                continue;

            yield return ResidualHitFactory.Create(
                ResidualCode,
                Owner,
                Policy,
                relativePath,
                lambda,
                "async lambda must be lowered (e.g. to CompletableFuture) by a preprocessor before Java conversion");
        }
    }
}
