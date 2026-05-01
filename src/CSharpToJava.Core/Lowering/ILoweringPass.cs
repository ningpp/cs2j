using CSharpToJava.Core.Context;
using CSharpToJava.Core.Java2;

namespace CSharpToJava.Core.Lowering;

public interface ILoweringPass
{
    string Name { get; }
    IrCompilationUnit Apply(IrCompilationUnit unit, ConversionContext context);
}
