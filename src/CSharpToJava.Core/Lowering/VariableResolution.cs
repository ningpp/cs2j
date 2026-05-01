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
        foreach (var method in type.Methods) if (method.Body != null) RenameDuplicates(method.Body);
        if (type is IrClassDeclaration cls) foreach (var ctor in cls.Constructors) if (ctor.Body != null) RenameDuplicates(ctor.Body);
        foreach (var nested in type.NestedTypes) LowerType(nested);
    }

    private void RenameDuplicates(IrBlockStatement block)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        FindDeclaredVars(block, seen, renames);
        if (renames.Count > 0) ApplyRenames(block, renames);
    }

    private void FindDeclaredVars(IrBlockStatement block, Dictionary<string, int> seen, Dictionary<string, string> renames)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrVariableDeclarationStatement vd)
            {
                if (seen.TryGetValue(vd.Name, out var count))
                {
                    seen[vd.Name] = count + 1;
                    var newName = vd.Name + "_" + count;
                    renames[vd.Name] = newName;
                    vd.Name = newName;
                }
                else seen[vd.Name] = 0;
            }
            if (stmt is IrBlockStatement inner) FindDeclaredVars(inner, seen, renames);
        }
    }

    private void ApplyRenames(IrBlockStatement block, Dictionary<string, string> renames)
    {
        foreach (var stmt in block.Statements)
        {
            if (stmt is IrExpressionStatement es) es.Expression = RenameInExpr(es.Expression, renames);
            if (stmt is IrReturnStatement rs && rs.Expression != null) rs.Expression = RenameInExpr(rs.Expression, renames);
            if (stmt is IrBlockStatement b) ApplyRenames(b, renames);
        }
    }

    private IrExpression RenameInExpr(IrExpression expr, Dictionary<string, string> renames)
    {
        if (expr is IrIdentifierExpression id && renames.TryGetValue(id.Name, out var newName))
            id.Name = newName;
        return expr;
    }
}
