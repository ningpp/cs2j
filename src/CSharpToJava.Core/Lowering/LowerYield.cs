using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerYield : ILoweringPass
{
    public string Name => "LowerYield";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        bool changed = true;
        while (changed) { changed = false;
        foreach (var method in type.Methods)
        {
            if (method.Body != null)
            {
                var (wasChanged, newStmts) = ReplaceYieldStatements(method.Body.Statements);
                if (wasChanged) { method.Body.Statements.Clear(); method.Body.Statements.AddRange(newStmts); changed = true; }
            }
        }}
        if (type is IrClassDeclaration cls) foreach (var ctor in cls.Constructors) if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private (bool changed, List<IrStatement> stmts) ReplaceYieldStatements(List<IrStatement> statements)
    {
        bool changed = false;
        var result = new List<IrStatement>();
        foreach (var stmt in statements)
        {
            if (stmt is IrCSharpYieldReturnStatement yr)
            {
                changed = true;
                result.Add(new IrReturnStatement { Expression = yr.Expression });
            }
            else if (stmt is IrCSharpYieldBreakStatement)
            {
                changed = true;
                result.Add(new IrReturnStatement { Expression = new IrLiteralExpression { Value = "null" } });
            }
            else result.Add(stmt);
        }
        return (changed, result);
    }

    private void LowerBlock(IrBlockStatement block) { }
}
