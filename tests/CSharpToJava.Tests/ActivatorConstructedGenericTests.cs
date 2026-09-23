using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Activator.CreateInstance(constructedGenericType, ..., args: null) succeeds
/// because the open generic type has an implicit parameterless constructor. The
/// Java conversion injects Class&lt;?&gt; runtime parameters into converter
/// constructors, so the reflection call must forward the MakeGenericType type
/// arguments. Regression: ObjectConverterFactory / IEnumerableConverterFactory.
/// </summary>
public class ActivatorConstructedGenericTests
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
