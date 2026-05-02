using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using CSharpToJava.Core.Transformers.Member;

namespace CSharpToJava.Core.Lowering;

public class LowerOperator : ILoweringPass
{
    public string Name => "LowerOperator";

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
            case IrIfStatement ifs: ifs.Condition = LowerExpression(ifs.Condition); ifs.ThenBody = LowerStatement(ifs.ThenBody); if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody); return ifs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        switch (expr)
        {
            case IrCSharpOperatorCallExpression opCall:
            {
                // Translate operator name if still in Roslyn format (e.g. "op_Multiply" → "multiply")
                var methodName = OperatorTransformer.OpSymbolToJavaName.TryGetValue(opCall.OperatorMethodName, out var n)
                    ? n : opCall.OperatorMethodName;

                var inv = new IrInvocationExpression
                {
                    MethodName = methodName,
                    Symbol = opCall.Symbol, JavaType = opCall.JavaType,
                };
                if (opCall.Left != null && opCall.Right != null)
                {
                    inv.Arguments.Add(LowerExpression(opCall.Left));
                    inv.Arguments.Add(LowerExpression(opCall.Right));
                }
                return inv;
            }
            case IrBinaryExpression bin: bin.Left = LowerExpression(bin.Left); bin.Right = LowerExpression(bin.Right); return bin;
            default: return expr;
        }
    }
}
