using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# struct enum fields default to the zero-valued member, never null. Java
/// enum references default to null, so the generated struct default constructor
/// must initialize them (e.g. WriteStackFrame.propertyState).
/// </summary>
public class StructEnumFieldDefaultTests
{

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
