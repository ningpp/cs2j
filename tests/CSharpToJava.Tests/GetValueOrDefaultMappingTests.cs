using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Nullable&lt;T&gt;.GetValueOrDefault() is converted to a
/// null-coalescing ternary in Java, since Java boxed types lack this method.
/// The LINQ rewriter emits ((items as ICollection&lt;T&gt;)?.Count).GetValueOrDefault()
/// which must produce  (expr != null ? expr : 0)  in Java.
/// </summary>
public class GetValueOrDefaultMappingTests
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
