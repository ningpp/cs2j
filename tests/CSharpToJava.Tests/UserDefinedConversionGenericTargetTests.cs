using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for user-defined conversion operators whose target type is generic
/// (e.g. implicit operator ReadOnlyMemory&lt;T&gt;(T[]) from the compat memory
/// types). The old "to&lt;TargetType&gt;" static-helper synthesis produced invalid
/// Java such as ReadOnlyMemory&lt;Integer&gt;.toReadOnlyMemory&lt;Integer&gt;(...);
/// generic targets must instead be wrapped with the compat constructor.
/// </summary>
public class UserDefinedConversionGenericTargetTests
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
