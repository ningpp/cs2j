using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public class HIRImportResolver
{
    public void ProcessUsing(UsingDirectiveSyntax node, IrCompilationUnit unit, ConversionContext ctx)
    {
        if (node.Name == null) return;

        if (node.StaticKeyword.Kind() == SyntaxKind.StaticKeyword)
        {
            var javaType = MapUsingToJava(node.Name.ToString(), ctx);
            if (!string.IsNullOrEmpty(javaType) && !unit.Imports.Contains("static " + javaType))
                unit.Imports.Add("static " + javaType);
            return;
        }

        if (node.Alias != null)
        {
            var target = ResolveAliasTarget(node, ctx);
            if (target != null)
                ctx.RegisterUsingAlias(node.Alias.Name.Identifier.Text, target, node.GetLocation());
            return;
        }

        var importName = MapUsingToJava(node.Name.ToString(), ctx);
        if (!string.IsNullOrEmpty(importName) && !unit.Imports.Contains(importName))
            unit.Imports.Add(importName);
    }

    private static Microsoft.CodeAnalysis.ITypeSymbol? ResolveAliasTarget(UsingDirectiveSyntax node, ConversionContext ctx)
    {
        var semModel = ctx.SemanticModel;
        if (semModel == null) return null;
        var info = semModel.GetSymbolInfo(node.Name!);
        return info.Symbol as Microsoft.CodeAnalysis.ITypeSymbol;
    }

    private static string? MapUsingToJava(string csharpUsing, ConversionContext ctx)
    {
        return csharpUsing switch
        {
            "System" => "java.lang",
            "System.Collections.Generic" => "java.util",
            "System.Linq" => "java.util.stream",
            _ => ctx.NamespaceToPackage(csharpUsing),
        };
    }
}
