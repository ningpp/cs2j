using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that reflection flag enums (MethodAttributes, CallingConventions,
/// TypeAttributes) map to the compat int-constants classes so bitwise usage
/// compiles. Reproduces RegexLWCGCompiler/RegexCompiler: javac "package
/// System.Reflection does not exist" for the unmapped flag enums.
/// </summary>
public class ReflectionFlagEnumMappingTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
