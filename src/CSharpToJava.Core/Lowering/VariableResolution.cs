using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class VariableResolution : ILoweringPass
{
    public string Name => "VariableResolution";

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        foreach (var type in unit.TypeDeclarations) LowerType(type);
        return unit;
    }

    private void LowerType(IrTypeDeclaration type)
    {
        foreach (var method in type.Methods) if (method.Body != null) DeduplicateInBlock(method.Body);
        if (type is IrClassDeclaration cls) foreach (var ctor in cls.Constructors) if (ctor.Body != null) DeduplicateInBlock(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private static void DeduplicateInBlock(IrBlockStatement block)
    {
        var scopeNames = new Dictionary<string, int>(StringComparer.Ordinal);
        // First pass: rename declarations
        RenameDeclarationsInBlock(block, scopeNames);
        // Second pass: rename references in expressions
        RenameReferencesInBlock(block, scopeNames);
    }

    private static void RenameDeclarationsInBlock(IrBlockStatement block, Dictionary<string, int> scopeNames)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrVariableDeclarationStatement vd)
            {
                if (scopeNames.TryGetValue(vd.Name, out var count))
                {
                    count++;
                    scopeNames[vd.Name] = count;
                    vd.Name = vd.Name + "_" + count;
                }
                else
                {
                    scopeNames[vd.Name] = 0;
                }
            }
            if (stmt is IrBlockStatement inner) RenameDeclarationsInBlock(inner, scopeNames);
            if (stmt is IrForEachStatement fe && scopeNames.TryGetValue(fe.VariableName, out var feCount))
            {
                feCount++;
                scopeNames[fe.VariableName] = feCount;
                fe.VariableName = fe.VariableName + "_" + feCount;
            }
        }
    }

    private static void RenameReferencesInBlock(IrBlockStatement block, Dictionary<string, int> scopeNames)
    {
        foreach (var stmt in block.Statements)
        {
            switch (stmt)
            {
                case IrExpressionStatement es: es.Expression = RenameInExpr(es.Expression, scopeNames); break;
                case IrReturnStatement rs: if (rs.Expression != null) rs.Expression = RenameInExpr(rs.Expression, scopeNames); break;
                case IrVariableDeclarationStatement vd: if (vd.Initializer != null) vd.Initializer = RenameInExpr(vd.Initializer, scopeNames); break;
                case IrIfStatement ifs:
                    ifs.Condition = RenameInExpr(ifs.Condition, scopeNames);
                    RenameReferencesInStatement(ifs.ThenBody, scopeNames);
                    if (ifs.ElseBody != null) RenameReferencesInStatement(ifs.ElseBody, scopeNames);
                    break;
                case IrForEachStatement fe:
                    fe.Collection = RenameInExpr(fe.Collection, scopeNames);
                    RenameReferencesInStatement(fe.Body, scopeNames);
                    break;
                case IrForStatement f:
                    if (f.Condition != null) f.Condition = RenameInExpr(f.Condition, scopeNames);
                    RenameReferencesInStatement(f.Body, scopeNames);
                    break;
                case IrWhileStatement w:
                    w.Condition = RenameInExpr(w.Condition, scopeNames);
                    RenameReferencesInStatement(w.Body, scopeNames);
                    break;
                case IrBlockStatement b: RenameReferencesInBlock(b, scopeNames); break;
            }
        }
    }

    private static void RenameReferencesInStatement(IrStatement stmt, Dictionary<string, int> scopeNames)
    {
        if (stmt is IrBlockStatement b) RenameReferencesInBlock(b, scopeNames);
        else if (stmt is IrExpressionStatement es) es.Expression = RenameInExpr(es.Expression, scopeNames);
        else if (stmt is IrReturnStatement rs && rs.Expression != null) rs.Expression = RenameInExpr(rs.Expression, scopeNames);
    }

    private static IrExpression RenameInExpr(IrExpression expr, Dictionary<string, int> scopeNames)
    {
        switch (expr)
        {
            case IrIdentifierExpression id:
                if (scopeNames.TryGetValue(id.Name, out var count) && count > 0)
                    id.Name = id.Name + "_" + count;
                return id;
            case IrBinaryExpression bin:
                bin.Left = RenameInExpr(bin.Left, scopeNames);
                bin.Right = RenameInExpr(bin.Right, scopeNames);
                return bin;
            case IrUnaryExpression un:
                un.Operand = RenameInExpr(un.Operand, scopeNames);
                return un;
            case IrConditionalExpression cond:
                cond.Condition = RenameInExpr(cond.Condition, scopeNames);
                cond.WhenTrue = RenameInExpr(cond.WhenTrue, scopeNames);
                cond.WhenFalse = RenameInExpr(cond.WhenFalse, scopeNames);
                return cond;
            case IrCastExpression cast:
                cast.Expression = RenameInExpr(cast.Expression, scopeNames);
                return cast;
            case IrNewExpression n:
                for (int i = 0; i < n.Arguments.Count; i++) n.Arguments[i] = RenameInExpr(n.Arguments[i], scopeNames);
                return n;
            case IrMemberAccessExpression mem:
                mem.Target = RenameInExpr(mem.Target, scopeNames);
                return mem;
            case IrInvocationExpression inv:
                if (inv.Target != null) inv.Target = RenameInExpr(inv.Target, scopeNames);
                for (int i = 0; i < inv.Arguments.Count; i++) inv.Arguments[i] = RenameInExpr(inv.Arguments[i], scopeNames);
                return inv;
            case IrAssignmentExpression asgn:
                asgn.Target = RenameInExpr(asgn.Target, scopeNames);
                asgn.Value = RenameInExpr(asgn.Value, scopeNames);
                return asgn;
            case IrArrayAccessExpression arr:
                arr.Target = RenameInExpr(arr.Target, scopeNames);
                arr.Index = RenameInExpr(arr.Index, scopeNames);
                return arr;
            case IrInstanceOfExpression inst:
                inst.Expression = RenameInExpr(inst.Expression, scopeNames);
                return inst;
            default: return expr;
        }
    }
}
