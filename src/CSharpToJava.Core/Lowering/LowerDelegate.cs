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
        if (type is IrClassDeclaration cls)
        {
            foreach (var ctor in cls.Constructors) if (ctor.Body != null) LowerBlock(ctor.Body);
            // Convert delegate types to functional interfaces
            for (int i = cls.NestedTypes.Count - 1; i >= 0; i--)
            {
                if (cls.NestedTypes[i] is IrClassDeclaration nested &&
                    nested.Annotations.Contains("FunctionalInterface"))
                {
                    LowerType(cls.NestedTypes[i]);
                }
            }
        }
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void LowerBlock(IrBlockStatement block)
    {
        for (int i = 0; i < block.Statements.Count; i++)
            block.Statements[i] = LowerStatement(block.Statements[i]);
    }

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
            case IrCSharpDelegateCreationExpression del:
                // Delegate creation -> inline the body as a lambda or anonymous class
                return LowerExpression(del.Body);
            case IrInvocationExpression inv:
                if (inv.Target != null) inv.Target = LowerExpression(inv.Target);
                for (int i = 0; i < inv.Arguments.Count; i++) inv.Arguments[i] = LowerExpression(inv.Arguments[i]);
                return inv;
            case IrBinaryExpression bin: bin.Left = LowerExpression(bin.Left); bin.Right = LowerExpression(bin.Right); return bin;
            case IrAssignmentExpression asgn: asgn.Target = LowerExpression(asgn.Target); asgn.Value = LowerExpression(asgn.Value); return asgn;
            default: return expr;
        }
    }
}
