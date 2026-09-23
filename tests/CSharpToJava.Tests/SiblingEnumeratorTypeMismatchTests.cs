using System;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the NetworkSimplex pattern where an outer class contains
/// sibling nested classes: one implementing IEnumerable&lt;T&gt; and another
/// implementing IEnumerator&lt;T&gt;. The IEnumerable&lt;T&gt; class returns
/// the sibling IEnumerator&lt;T&gt; from its GetEnumerator().
///
/// The converter must keep the IEnumerator&lt;T&gt; class as
/// CSharpGenericEnumerator&lt;T&gt; (not downgrade it to
/// CSharpGenericEnumerator&lt;Object&gt;), otherwise the iterator() return
/// type CSharpGenericEnumerator&lt;T&gt; cannot accept the new instance.
/// </summary>
public class SiblingEnumeratorTypeMismatchTests
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
