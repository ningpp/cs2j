using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Java cannot emit IL, so ReflectionEmitMemberAccessor must be replaced by the
/// reflection-based MemberAccessor implementation, and Activator.CreateInstance's
/// (Type, bool nonPublic) overload must not be treated as a params object[] arg.
/// Regression: System.Text.Json ArrayTests ILGenerator.Emit failures.
/// </summary>
public class MemberAccessorStrategyConversionTests
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
