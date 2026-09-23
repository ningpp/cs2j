using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Activator.CreateInstance(Type, object) uses a params object[] overload;
/// a single non-array object argument (including null) must become one element of
/// the argument array, not a cast of the object itself to Object[].
/// </summary>
public class ActivatorCreateInstanceMappingTests
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
