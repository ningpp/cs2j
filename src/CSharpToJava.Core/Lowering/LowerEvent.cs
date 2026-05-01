using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerEvent : ILoweringPass
{
    public string Name => "LowerEvent";

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
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpEventExpression ev when ev.IsSubscribe:
                return new IrInvocationExpression { Target = LowerExpression(ev.Target), MethodName = "add" + ev.EventName, Arguments = { LowerExpression(ev.Handler!) } };
            case IrCSharpEventExpression ev2 when ev2.IsUnsubscribe:
                return new IrInvocationExpression { Target = LowerExpression(ev2.Target), MethodName = "remove" + ev2.EventName, Arguments = { LowerExpression(ev2.Handler!) } };
            case IrCSharpEventExpression ev3 when ev3.IsRaise:
                return new IrInvocationExpression { Target = LowerExpression(ev3.Target), MethodName = ev3.EventName + "Listeners.forEach" };
            default: return expr;
        }
    }
}
