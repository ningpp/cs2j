using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.Normalization;

/// <summary>
/// NORM-POINTER-LOCAL：局部声明（含 fixed 声明）中的指针变量。
/// 显式 <c>T*</c>/<c>delegate*</c> 由语法形态命中；自然类型 <c>var p = ...</c>
/// 与隐式指针通过语义模型命中（与 Java 门禁一致）。
/// 对应门禁：StatementTransformer.TransformLocalDeclaration
/// （<c>ILocalSymbol.Type is IPointerTypeSymbol</c>）。
/// </summary>
internal sealed class PointerLocalResidualProbe : IResidualProbe
{
    internal const string ResidualCode = "NORM-POINTER-LOCAL";
    internal const string Owner = "unsafe-ffm";
    internal const ResidualPolicy Policy = ResidualPolicy.Fail;
    internal const string Gate = "pointer-typed local declaration";

    public string Code => ResidualCode;

    public IEnumerable<ResidualHit> Scan(SemanticModel model, string relativePath)
    {
        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes())
        {
            if (node is not VariableDeclarationSyntax declaration)
                continue;

            // 只覆盖 Java 门禁对应的局部声明形态：普通局部声明与 fixed 声明。
            var anchor = declaration.Parent switch
            {
                LocalDeclarationStatementSyntax local => (SyntaxNode)local,
                FixedStatementSyntax fixedStatement => fixedStatement,
                _ => null,
            };
            if (anchor is null)
                continue;

            var typeIsPointer = declaration.Type is PointerTypeSyntax or FunctionPointerTypeSyntax;
            foreach (var variable in declaration.Variables)
            {
                var local = model.GetDeclaredSymbol(variable) as ILocalSymbol;
                var symbolIsPointer =
                    local?.Type is IPointerTypeSymbol or IFunctionPointerTypeSymbol;
                if (!typeIsPointer && !symbolIsPointer)
                    continue;

                var name = local?.Name ?? variable.Identifier.Text;
                yield return ResidualHitFactory.Create(
                    ResidualCode,
                    Owner,
                    Policy,
                    relativePath,
                    anchor,
                    $"pointer local '{name}' must be lowered by a preprocessor before Java conversion");
            }
        }
    }
}
