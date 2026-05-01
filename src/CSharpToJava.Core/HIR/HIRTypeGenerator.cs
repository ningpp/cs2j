using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Core.HIR;

public class HIRTypeGenerator
{
    public IrTypeDeclaration? Generate(TypeDeclarationSyntax node, ConversionContext ctx) => throw new NotImplementedException();
    public IrEnumDeclaration? GenerateEnum(EnumDeclarationSyntax node, ConversionContext ctx) => throw new NotImplementedException();
    public IrClassDeclaration? GenerateDelegate(DelegateDeclarationSyntax node, ConversionContext ctx) => throw new NotImplementedException();
}
