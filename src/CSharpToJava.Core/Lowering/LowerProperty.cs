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
        if (prop.PropertyName is "CurrentThread" or "Zero" or "MaxValue" or "MinValue")
        {
            // （曾用于排查的 D:\tmp 调试写文件已移除——管线不应有隐藏副作用。）
        }

        // Static BCL property (Thread.CurrentThread, …): the receiver must be the
        // mapped compat type; the source identifier alone has no Java counterpart.
        if (propSymbol is { IsStatic: true } && propSymbol.ContainingType is INamedTypeSymbol staticOwner
            && staticOwner.TypeKind != TypeKind.Enum)
        {
            var mappedOwner = _ctx.MapType(staticOwner);
            if (mappedOwner.Contains('<'))
                mappedOwner = mappedOwner[..mappedOwner.IndexOf('<')];
            if (mappedOwner != staticOwner.Name)
            {
                foreach (var import in _ctx.TypeMappings.GetRequiredImports(
                             staticOwner.OriginalDefinition.ToDisplayString()))
                {
                    _ctx.AddImport(import);
                }
                var metadataKey = (staticOwner.ContainingNamespace?.ToDisplayString() is { Length: > 0 } ns
                    ? ns + "." : string.Empty) + staticOwner.OriginalDefinition.MetadataName;
                foreach (var import in _ctx.TypeMappings.GetRequiredImports(metadataKey))
                    _ctx.AddImport(import);

                return new IrInvocationExpression
                {
                    Target = new IrIdentifierExpression
                    {
                        Name = mappedOwner,
                        Symbol = staticOwner,
                        JavaType = mappedOwner,
                    },
                    MethodName = TryMapPropertyName(propSymbol, prop.PropertyName),
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }
        }

        if (propSymbol is { Name: "Size" }
            && propSymbol.ContainingType?.SpecialType == SpecialType.System_IntPtr)
        {
            _ctx.AddImport("io.github.ningpp.compat.IntPtrHelper");
            return new IrInvocationExpression
            {
                MethodName = "IntPtrHelper.getSize",
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        if (propSymbol?.ContainingType?.SpecialType == SpecialType.System_Array
            && prop.PropertyName == "Length"
            && IsJavaArrayType(prop.Target.JavaType))
        {
            return new IrMemberAccessExpression
            {
                Target = loweredTarget,
                MemberName = "length",
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // System.Array references map to CSharpArray in compact.
        if (propSymbol?.ContainingType?.SpecialType == SpecialType.System_Array)
        {
            if (prop.PropertyName == "Length")
            {
                return new IrInvocationExpression
                {
                    Target = loweredTarget,
                    MethodName = "getLength",
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }

            var mappedName = TryMapPropertyName(propSymbol, prop.PropertyName);
            return new IrMemberAccessExpression
            {
                Target = loweredTarget,
                MemberName = mappedName,
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // C# string.Length -> java.lang.String.length() (NOT getLength()).
        if (propSymbol?.ContainingType?.SpecialType == SpecialType.System_String
            && prop.PropertyName == "Length")
        {
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = "length",
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // Check TypeMappings for a configured method name (e.g. Count → size)
        var mappedMethod = TryMapPropertyName(propSymbol, prop.PropertyName);
        if (mappedMethod != prop.PropertyName)
        {
            // 点分静态 compat 助手映射（System.Exception.InnerException →
            // ExceptionCompat.getInnerException）：渲染为 Helper.method(receiver)，
            // 导入按映射名前缀注入（receiver 常是 Exception 子类，类型条目 imports
            // 覆盖不到；与 InvocationExpressionTransformer.AddCompatHelperImport 同款）。
            Transformers.Expression.InvocationExpressionTransformer
                .AddCompatHelperImport(mappedMethod, _ctx);
            // For System.Array properties mapped to field names, emit member access (no parens)
            if (propSymbol?.ContainingType?.SpecialType == SpecialType.System_Array)
            {
                return new IrMemberAccessExpression
                {
                    Target = loweredTarget,
                    MemberName = mappedMethod,
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = mappedMethod,
                Symbol = prop.Symbol,
                JavaType = prop.JavaType,
            };
        }

        // Generic fallback: Array.Length → .length (field), collection.Count → .size()
        if (propSymbol != null)
        {
            if (propSymbol.ContainingType?.TypeKind == TypeKind.Array && prop.PropertyName == "Length")
            {
                return new IrMemberAccessExpression
                {
                    Target = loweredTarget,
                    MemberName = "length",
                    Symbol = prop.Symbol,
                    JavaType = prop.JavaType,
                };
            }
            if (prop.PropertyName == "Count" && propSymbol.ContainingType is INamedTypeSymbol named)
            {
                foreach (var iface in named.AllInterfaces)
                {
                    var ifaceDisplay = iface.ToDisplayString();
                    if (ifaceDisplay.StartsWith("System.Collections.Generic.ICollection") ||
                        ifaceDisplay.StartsWith("System.Collections.Generic.IList") ||
                        ifaceDisplay.StartsWith("System.Collections.Generic.IReadOnlyCollection") ||
                        ifaceDisplay.StartsWith("System.Collections.Generic.ISet") ||
                        ifaceDisplay == "System.Collections.ICollection" ||
                        ifaceDisplay == "System.Collections.IList")
                    {
                        return new IrInvocationExpression
                        {
                            Target = loweredTarget,
                            MethodName = "size",
                            Symbol = prop.Symbol,
                            JavaType = prop.JavaType,
                        };
                    }
                }
            }
        }

        // Default: getter/setter pattern
        var javaName = char.ToUpper(prop.PropertyName[0]) + prop.PropertyName.Substring(1);
        var methodName = (prop.IsSetter ? "set" : "get") + javaName;
        if (!prop.IsSetter && methodName == "getClass")
            methodName = "getClassValue";
        return new IrInvocationExpression
        {
            Target = loweredTarget,
            MethodName = methodName,
            Symbol = prop.Symbol,
            JavaType = prop.JavaType,
        };
    }

    private IrExpression LowerPropertySet(IrCSharpPropertyAccessExpression setProp, IrExpression value)
    {
        var propSymbol = setProp.Symbol as IPropertySymbol;
        var loweredValue = LowerExpression(value);
        var loweredTarget = LowerExpression(setProp.Target);

        // Cs2jByRef holder writes use the value-returning compat alias (same rule
        // as AssignmentTransformer.Property): the setValue ABI returns void.
        if (Transformers.Expression.AssignmentTransformer.IsCs2jByRefHolderValue(propSymbol))
        {
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = "set",
                Arguments = { loweredValue },
                Symbol = setProp.Symbol,
            };
        }

        // 读取侧映射可能是字段风格 getter（StringBuilder.Length → length），不能
        // 当作 setter 调用。显式访问器映射 set_<Name>（StringBuilder.set_Length →
        // setLength）优先于属性名映射。
        var setterAccessor = "set_" + setProp.PropertyName;
        var explicitSetter = TryMapPropertyName(propSymbol, setterAccessor);
        if (explicitSetter != setterAccessor
            && !JavaNaming.IsFieldStyleAccessorName(explicitSetter))
        {
            return new IrInvocationExpression
            {
                Target = loweredTarget,
                MethodName = explicitSetter,
                Arguments = { loweredValue },
                Symbol = setProp.Symbol,
            };
        }

        // Check TypeMappings for a configured setter name; a field-style mapped
        // name is the read accessor spelling and must fall back to bean-style.
        var mappedMethod = TryMapPropertyName(propSymbol, setProp.PropertyName);
        if (mappedMethod != setProp.PropertyName
            && !JavaNaming.IsFieldStyleAccessorName(mappedMethod))
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

    private static bool IsJavaArrayType(string? javaType)
        => javaType?.TrimEnd().EndsWith("[]", StringComparison.Ordinal) == true;
}
