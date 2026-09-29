using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that an enum with a member literally named "value" does not collide with
/// the synthetic value field emitted by EnumTransformer.
/// Reproduces ExceptionArgument: javac "variable value is already defined in enum".
/// </summary>
public class EnumValueMemberCollisionTests
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
