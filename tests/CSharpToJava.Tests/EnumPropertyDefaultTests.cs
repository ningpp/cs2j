using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression (AGL SolverShellTests): C# enum auto-properties default to the
/// member with value 0, but Java enum fields default to null. The generated
/// backing field must be initialized to the 0-valued member.
/// </summary>
public class EnumPropertyDefaultTests
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
