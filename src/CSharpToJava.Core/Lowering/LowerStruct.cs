using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerStruct : ILoweringPass
{
    public string Name => "LowerStruct";

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
            case IrVariableDeclarationStatement vd: if (vd.Initializer != null) vd.Initializer = LowerExpression(vd.Initializer); return vd;
            case IrReturnStatement rs: if (rs.Expression != null) rs.Expression = LowerExpression(rs.Expression); return rs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpStructCopyExpression copy:
                return new IrInvocationExpression { Target = LowerExpression(copy.Source), MethodName = "clone", JavaType = copy.StructType };
            case IrAssignmentExpression asgn:
                asgn.Target = LowerExpression(asgn.Target); asgn.Value = LowerExpression(asgn.Value); return asgn;
            default: return expr;
        }
    }
}
