using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Transformers;

namespace CSharpToJava.Core.Lowering;

public class LowerRefOut : ILoweringPass
{
    public string Name => "LowerRefOut";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods)
            if (method.Body != null) LowerBlock(method.Body);
        if (type is IrClassDeclaration cls)
            foreach (var ctor in cls.Constructors)
                if (ctor.Body != null) LowerBlock(ctor.Body);
        // Remove ref/out markers from parameters
        foreach (var method in type.Methods)
            foreach (var param in method.Parameters)
            {
                if (param.IsReadOnlyRef) { param.IsRef = false; param.IsReadOnlyRef = false; }
                param.IsOut = false; param.IsRef = false;
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
            case IrReturnStatement ret: if (ret.Expression != null) ret.Expression = LowerExpression(ret.Expression); return ret;
            case IrIfStatement ifs: ifs.Condition = LowerExpression(ifs.Condition); ifs.ThenBody = LowerStatement(ifs.ThenBody); if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody); return ifs;
            case IrForEachStatement fe: fe.Collection = LowerExpression(fe.Collection); fe.Body = LowerStatement(fe.Body); return fe;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrInvocationExpression inv:
                for (int i = 0; i < inv.Arguments.Count; i++)
                {
                    if (inv.Arguments[i] is IrCSharpRefOutExpression refOut)
                    {
                        if (refOut.IsOut)
                    {
                        var holderType = HolderTypeResolver.GetHolderType(refOut.Inner.JavaType ?? "Object");
                        var holderInit = HolderTypeResolver.GetHolderInstantiation(holderType);
                        inv.Arguments[i] = new IrNewExpression { TypeName = holderInit };
                    }
                        else if (refOut.IsRef || refOut.IsReadOnlyRef)
                            inv.Arguments[i] = LowerExpression(refOut.Inner);
                    }
                    else inv.Arguments[i] = LowerExpression(inv.Arguments[i]);
                }
                if (inv.Target != null) inv.Target = LowerExpression(inv.Target);
                return inv;
            case IrCSharpRefOutExpression reo: return LowerExpression(reo.Inner);
            case IrBinaryExpression bin: bin.Left = LowerExpression(bin.Left); bin.Right = LowerExpression(bin.Right); return bin;
            case IrAssignmentExpression asgn: asgn.Target = LowerExpression(asgn.Target); asgn.Value = LowerExpression(asgn.Value); return asgn;
            default: return expr;
        }
    }
}
