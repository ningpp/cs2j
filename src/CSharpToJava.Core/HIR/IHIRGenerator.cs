using Microsoft.CodeAnalysis.CSharp.Syntax;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.HIR;

public interface IHIRGenerator
{
    IrCompilationUnit Generate(CompilationUnitSyntax root, ConversionContext context);
}
