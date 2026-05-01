using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerProperty : ILoweringPass
{
    public string Name => "LowerProperty";

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
            case IrIfStatement ifs: ifs.Condition = LowerExpression(ifs.Condition); ifs.ThenBody = LowerStatement(ifs.ThenBody); if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody); return ifs;
            case IrForEachStatement fe: fe.Collection = LowerExpression(fe.Collection); fe.Body = LowerStatement(fe.Body); return fe;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpPropertyAccessExpression prop:
                var javaName = char.ToUpper(prop.PropertyName[0]) + prop.PropertyName.Substring(1);
                return new IrInvocationExpression
                {
                    Target = LowerExpression(prop.Target),
                    MethodName = (prop.IsSetter ? "set" : "get") + javaName,
                    Symbol = prop.Symbol, JavaType = prop.JavaType,
                };
            case IrAssignmentExpression asgn when asgn.Target is IrCSharpPropertyAccessExpression setProp:
                var lowered = (IrInvocationExpression)LowerExpression(setProp);
                lowered.Arguments.Add(LowerExpression(asgn.Value));
                return lowered;
            case IrCSharpIndexerAccessExpression idx:
                var inv = new IrInvocationExpression { Target = LowerExpression(idx.Target), MethodName = idx.IsSetter ? "set" : "get", Symbol = idx.Symbol };
                foreach (var i in idx.Indices) inv.Arguments.Add(LowerExpression(i));
                return inv;
            case IrInvocationExpression call:
                if (call.Target != null) call.Target = LowerExpression(call.Target);
                for (int i = 0; i < call.Arguments.Count; i++) call.Arguments[i] = LowerExpression(call.Arguments[i]);
                return call;
            case IrBinaryExpression bin: bin.Left = LowerExpression(bin.Left); bin.Right = LowerExpression(bin.Right); return bin;
            case IrAssignmentExpression asgn2: asgn2.Target = LowerExpression(asgn2.Target); asgn2.Value = LowerExpression(asgn2.Value); return asgn2;
            case IrMemberAccessExpression mem: mem.Target = LowerExpression(mem.Target); return mem;
            default: return expr;
        }
    }
}
