using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public class HIRTypeGenerator
{
    public IrTypeDeclaration? Generate(TypeDeclarationSyntax node, ConversionContext ctx)
    {
        return node.Kind() switch
        {
            SyntaxKind.ClassDeclaration => GenerateClass((ClassDeclarationSyntax)node, ctx),
            SyntaxKind.InterfaceDeclaration => GenerateInterface((InterfaceDeclarationSyntax)node, ctx),
            SyntaxKind.StructDeclaration => GenerateStruct((StructDeclarationSyntax)node, ctx),
            SyntaxKind.RecordDeclaration => GenerateRecord((RecordDeclarationSyntax)node, ctx),
            _ => null,
        };
    }

    private IrClassDeclaration GenerateClass(TypeDeclarationSyntax node, ConversionContext ctx)
    {
        ctx.EnterType(null!);
        var cls = new IrClassDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
        };
        if (node.BaseList != null)
        {
            foreach (var bt in node.BaseList.Types)
            {
                var mapped = ctx.MapTypeFromSyntax(bt.Type);
                var isInterface = IsInterfaceBase(bt, ctx);
                if (isInterface)
                    cls.ImplementedTypes.Add(mapped);
                else if (cls.ExtendedType == null)
                    cls.ExtendedType = mapped;
                else
                    cls.ImplementedTypes.Add(mapped);
            }
        }
        foreach (var member in node.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                    {
                        cls.Fields.Add(new IrFieldDeclaration
                        {
                            Modifiers = MapModifiers(f.Modifiers),
                            Type = ctx.MapTypeFromSyntax(f.Declaration.Type),
                            Name = v.Identifier.Text,
                            Initializer = v.Initializer?.Value.ToString(),
                        });
                    }
                    break;
                case MethodDeclarationSyntax m:
                    cls.Methods.Add(GenerateMethod(m, ctx));
                    break;
                case ConstructorDeclarationSyntax c:
                    cls.Constructors.Add(GenerateConstructor(c, ctx));
                    break;
                case TypeDeclarationSyntax nested:
                    var nestedType = Generate(nested, ctx);
                    if (nestedType != null) cls.NestedTypes.Add(nestedType);
                    break;
                case EnumDeclarationSyntax nestedEnum:
                    var enm = GenerateEnumInternal(nestedEnum, ctx);
                    if (enm != null) cls.NestedTypes.Add(enm);
                    break;
            }
        }
        ctx.LeaveType();
        return cls;
    }

    private IrInterfaceDeclaration GenerateInterface(InterfaceDeclarationSyntax node, ConversionContext ctx)
    {
        var iface = new IrInterfaceDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
        };
        if (node.BaseList != null)
        {
            foreach (var bt in node.BaseList.Types)
                iface.ExtendedTypes.Add(ctx.MapTypeFromSyntax(bt.Type));
        }
        foreach (var member in node.Members)
        {
            if (member is MethodDeclarationSyntax m)
                iface.Methods.Add(GenerateMethod(m, ctx));
        }
        return iface;
    }

    private IrClassDeclaration GenerateStruct(StructDeclarationSyntax node, ConversionContext ctx)
    {
        var cls = GenerateClass(node, ctx);
        cls!.IsConvertedFromStruct = true;
        return cls;
    }

    private IrClassDeclaration GenerateRecord(RecordDeclarationSyntax node, ConversionContext ctx)
    {
        var cls = GenerateClass(node, ctx);
        cls!.IsRecord = true;
        return cls;
    }

    public IrEnumDeclaration? GenerateEnum(EnumDeclarationSyntax node, ConversionContext ctx)
    {
        return GenerateEnumInternal(node, ctx);
    }

    private IrEnumDeclaration? GenerateEnumInternal(EnumDeclarationSyntax node, ConversionContext ctx)
    {
        var enm = new IrEnumDeclaration
        {
            Name = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
        };
        foreach (var member in node.Members)
        {
            var val = new IrEnumValue { Name = member.Identifier.Text };
            if (member.EqualsValue != null)
                val.Arguments.Add(new IrLiteralExpression { Value = member.EqualsValue.Value.ToString() });
            enm.Values.Add(val);
        }
        return enm;
    }

    public IrInterfaceDeclaration? GenerateDelegate(DelegateDeclarationSyntax node, ConversionContext ctx)
    {
        var sym = ctx.SemanticModel?.GetDeclaredSymbol(node);
        if (sym is not INamedTypeSymbol namedType) return null;
        var invokeMethod = namedType.DelegateInvokeMethod;
        if (invokeMethod == null) return null;
        var delegateMethod = new IrMethodDeclaration
        {
            Name = invokeMethod.Name,
            ReturnType = ctx.MapType(invokeMethod.ReturnType),
            Modifiers = IrModifiers.Public | IrModifiers.Abstract,
        };
        delegateMethod.Parameters.AddRange(invokeMethod.Parameters.Select(p => new IrParameter { Type = ctx.MapType(p.Type), Name = p.Name }));
        return new IrInterfaceDeclaration
        {
            Name = ctx.MapType(namedType),
            Modifiers = IrModifiers.Public,
            Annotations = { "FunctionalInterface" },
            Methods = { delegateMethod },
        };
    }

    private IrMethodDeclaration GenerateMethod(MethodDeclarationSyntax node, ConversionContext ctx)
    {
        var semModel = ctx.SemanticModel;
        var symbol = semModel?.GetDeclaredSymbol(node) as IMethodSymbol;
        var returnType = symbol != null ? ctx.MapType(symbol.ReturnType) : ctx.MapTypeFromSyntax(node.ReturnType);
        var method = new IrMethodDeclaration
        {
            Name = node.Identifier.Text,
            ReturnType = returnType,
            Modifiers = MapModifiers(node.Modifiers),
        };
        method.Parameters.AddRange(node.ParameterList.Parameters.Select(p => new IrParameter
        {
            Type = ctx.MapTypeFromSyntax(p.Type!),
            Name = p.Identifier.Text,
            IsRef = p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)),
            IsOut = p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)),
        }));
        if (node.Body != null)
        {
            var stmtGen = new HIRStatementGenerator();
            method.Body = stmtGen.GenerateBlock(node.Body, ctx);
        }
        return method;
    }

    private IrConstructorDeclaration GenerateConstructor(ConstructorDeclarationSyntax node, ConversionContext ctx)
    {
        var ctor = new IrConstructorDeclaration
        {
            TypeName = node.Identifier.Text,
            Modifiers = MapModifiers(node.Modifiers),
            Body = node.Body != null ? new HIRStatementGenerator().GenerateBlock(node.Body, ctx) : new IrBlockStatement(),
        };
        ctor.Parameters.AddRange(node.ParameterList.Parameters.Select(p => new IrParameter { Type = ctx.MapTypeFromSyntax(p.Type!), Name = p.Identifier.Text }));
        return ctor;
    }

    private static IrModifiers MapModifiers(SyntaxTokenList modifiers)
    {
        var m = IrModifiers.None;
        foreach (var tok in modifiers)
        {
            m |= tok.Kind() switch
            {
                SyntaxKind.PublicKeyword => IrModifiers.Public,
                SyntaxKind.ProtectedKeyword => IrModifiers.Protected,
                SyntaxKind.PrivateKeyword => IrModifiers.Private,
                SyntaxKind.StaticKeyword => IrModifiers.Static,
                SyntaxKind.AbstractKeyword => IrModifiers.Abstract,
                _ => IrModifiers.None,
            };
        }
        return m;
    }

    private static bool IsInterfaceBase(BaseTypeSyntax bt, ConversionContext ctx)
    {
        // Heuristic: if the first character is 'I' followed by uppercase, it's likely an interface
        var name = bt.Type.ToString();
        var shortName = name.Split('.').Last();
        return shortName.Length >= 2 && shortName[0] == 'I' && char.IsUpper(shortName[1]);
    }
}
