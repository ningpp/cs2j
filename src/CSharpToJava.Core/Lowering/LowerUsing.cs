using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerUsing : ILoweringPass
{
    public string Name => "LowerUsing";

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

    private void LowerBlock(IrBlockStatement block)
    {
        var newStmts = new List<IrStatement>();
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrCSharpUsingStatement us)
            {
                var tryBody = new IrBlockStatement();
                if (us.Resource != null) tryBody.Statements.Add(us.Resource);
                if (us.Body is IrBlockStatement b) tryBody.Statements.AddRange(b.Statements);
                else tryBody.Statements.Add(us.Body);
                var finallyBody = new IrBlockStatement();
                var resName = us.Resource?.Name ?? "_res";
                finallyBody.Statements.Add(new IrExpressionStatement { Expression = new IrInvocationExpression { Target = new IrIdentifierExpression { Name = resName }, MethodName = "close" } });
                newStmts.Add(new IrTryCatchStatement { TryBody = tryBody, FinallyBody = finallyBody });
            }
            else newStmts.Add(stmt);
        }
        block.Statements.Clear(); block.Statements.AddRange(newStmts);
        foreach (var s in block.Statements) if (s is not IrCSharpUsingStatement) LowerStatement(s);
    }

    private void LowerStatement(IrStatement stmt) { if (stmt is IrBlockStatement b) LowerBlock(b); }
}
