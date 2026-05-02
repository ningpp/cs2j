using Microsoft.CodeAnalysis;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public class LowerProperty : ILoweringPass
{
    public string Name => "LowerProperty";

    private ConversionContext _ctx = null!;

    public IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context)
    {
        _ctx = context;
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
                return LowerPropertyAccess(prop);
            case IrAssignmentExpression asgn when asgn.Target is IrCSharpPropertyAccessExpression setProp:
                return LowerPropertySet(setProp, asgn.Value);
            // IrCSharpIndexerAccessExpression handled by LowerIndexer
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

    private IrExpression LowerPropertyAccess(IrCSharpPropertyAccessExpression prop)
    {
        var propSymbol = prop.Symbol as IPropertySymbol;
        var loweredTarget = LowerExpression(prop.Target);

        // System.Array.Length → .length (Java array field, not a method)
        if (propSymbol?.ContainingType?.SpecialType == SpecialType.System_Array)
        {
            var mappedName = TryMapPropertyName(propSymbol, prop.PropertyName);
            return new IrMemberAccessExpression
            {
                Target = loweredTarget,
                MemberName = mappedName,
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // Check TypeMappings for a configured method name (e.g. Count → size)
        var mappedMethod = TryMapPropertyName(propSymbol, prop.PropertyName);
        if (mappedMethod != prop.PropertyName)
        {
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = mappedMethod,
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // Default: getter/setter pattern
        var javaName = char.ToUpper(prop.PropertyName[0]) + prop.PropertyName.Substring(1);
        return new IrInvocationExpression
        {
            Target = loweredTarget,
            MethodName = (prop.IsSetter ? "set" : "get") + javaName,
            Symbol = prop.Symbol,
            JavaType = prop.JavaType,
        };
    }

    private IrExpression LowerPropertySet(IrCSharpPropertyAccessExpression setProp, IrExpression value)
    {
        var propSymbol = setProp.Symbol as IPropertySymbol;
        var loweredValue = LowerExpression(value);
        var loweredTarget = LowerExpression(setProp.Target);

        // Check TypeMappings for a configured setter name
        var mappedMethod = TryMapPropertyName(propSymbol, setProp.PropertyName);
        if (mappedMethod != setProp.PropertyName)
        {
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = mappedMethod,
                Arguments = { loweredValue },
                Symbol = setProp.Symbol,
            };
        }

        var javaSetterName = char.ToUpper(setProp.PropertyName[0]) + setProp.PropertyName.Substring(1);
        return new IrInvocationExpression
        {
            Target = loweredTarget,
            MethodName = "set" + javaSetterName,
            Arguments = { loweredValue },
            Symbol = setProp.Symbol,
        };
    }

    /// <summary>
    /// Looks up a property name in TypeMappings method mappings.
    /// Walks the interface hierarchy when the containing type is a class.
    /// Returns the mapped Java method name, or the original C# name if no mapping exists.
    /// </summary>
    private string TryMapPropertyName(IPropertySymbol? propSymbol, string csharpName)
    {
        if (propSymbol == null) return csharpName;

        var containingType = propSymbol.ContainingType;
        if (containingType == null) return csharpName;

        // Try the concrete type first
        var typeName = containingType.ToDisplayString();
        var mapped = _ctx.TypeMappings.MapMethod(typeName, csharpName);
        if (mapped != null) return mapped;

        // Try original definition
        var originalDef = containingType.OriginalDefinition;
        if (originalDef != null && !SymbolEqualityComparer.Default.Equals(originalDef, containingType))
        {
            var originalName = originalDef.ToDisplayString();
            mapped = _ctx.TypeMappings.MapMethod(originalName, csharpName);
            if (mapped != null) return mapped;
        }

        // Walk interface hierarchy (e.g. a class implements ICollection<T> which has Count→size)
        foreach (var iface in containingType.AllInterfaces)
        {
            var ifaceName = iface.ToDisplayString();
            mapped = _ctx.TypeMappings.MapMethod(ifaceName, csharpName);
            if (mapped != null) return mapped;

            var ifaceOriginal = iface.OriginalDefinition;
            if (ifaceOriginal != null && !SymbolEqualityComparer.Default.Equals(ifaceOriginal, iface))
            {
                mapped = _ctx.TypeMappings.MapMethod(ifaceOriginal.ToDisplayString(), csharpName);
                if (mapped != null) return mapped;
            }
        }

        return csharpName;
    }
}
