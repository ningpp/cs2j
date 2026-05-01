using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerIndexer : ILoweringPass
{
    public string Name => "LowerIndexer";

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
        switch (stmt)
        {
            case IrBlockStatement b: LowerBlock(b); return b;
            case IrExpressionStatement es: es.Expression = LowerExpression(es.Expression); return es;
            case IrReturnStatement rs: if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression); return rs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        if (expr is IrAssignmentExpression asgn && asgn.Target is IrCSharpIndexerAccessExpression idx)
        {
            return new IrInvocationExpression
            {
                Target = LowerExpression(idx.Target),
                MethodName = "set",
                Arguments = { LowerExpression(idx.Indices[0]), LowerExpression(asgn.Value) },
            };
        }
        return expr;
    }
}
