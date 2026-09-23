using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that non-generic System.Collections.ICollection parameters accept generic
/// collection arguments (List&lt;T&gt;, etc.) in the generated Java.
/// In C#, List&lt;T&gt; implements both ICollection&lt;T&gt; (generic) and ICollection
/// (non-generic). But in the Java compat library, CSharpList&lt;T&gt; only implements
/// CSharpICollection&lt;T&gt;, not the raw CSharpCollection interface. So a non-generic
/// ICollection parameter must be emitted as CSharpICollection&lt;?&gt; to accept
/// CSharpList&lt;T&gt; arguments.
/// </summary>
public class NonGenericICollectionParameterTests
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
