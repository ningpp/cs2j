using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerDelegate : ILoweringPass
{
    public string Name => "LowerDelegate";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods) if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls) foreach (var ctor in cls.Constructors) if (ctor.Body != null) LowerBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block) { for (int i = 0; i < block.Statements.Count; i++) block.Statements[i] = LowerStatement(block.Statements[i]); }

    private IrStatement LowerStatement(IrStatement stmt)
    {
        if (stmt is IrBlockStatement b) { LowerBlock(b); return b; }
        if (stmt is IrExpressionStatement es) { es.Expression = LowerExpression(es.Expression); return es; }
        return stmt;
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        if (expr is IrCSharpDelegateCreationExpression del) return LowerExpression(del.Body);
        return expr;
    }
}
