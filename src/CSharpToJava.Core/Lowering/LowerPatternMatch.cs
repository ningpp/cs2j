using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerPatternMatch : ILoweringPass
{
    public string Name => "LowerPatternMatch";

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
            case IrIfStatement ifs: ifs.Condition = LowerExpression(ifs.Condition); ifs.ThenBody = LowerStatement(ifs.ThenBody); if (ifs.ElseBody != null) ifs.ElseBody = LowerStatement(ifs.ElseBody); return ifs;
            default: return stmt;
        }
    }

    private IrExpression LowerExpression(IrExpression expr)
    {
        if (expr is IrCSharpPatternExpression pat && pat.PatternKind == "type" && pat.MatchedType != null)
        {
            // `is double d` on a boxed subject must test the wrapper type — Java's
            // primitive instance patterns are a preview feature (Java 28) and boxed
            // checks are semantically equivalent for Number subjects.
            var boxedType = BoxJavaTypeName(pat.MatchedType);
            return new IrInstanceOfExpression { Expression = LowerExpression(pat.Subject), TypeName = boxedType, PatternVariable = pat.PatternVariable };
        }
        return expr;
    }

    private static string BoxJavaTypeName(string typeName)
    {
        return typeName switch
        {
            "int" => "Integer",
            "long" => "Long",
            "double" => "Double",
            "float" => "Float",
            "short" => "Short",
            "byte" => "Byte",
            "boolean" => "Boolean",
            "char" => "Character",
            _ => typeName
        };
    }
}
