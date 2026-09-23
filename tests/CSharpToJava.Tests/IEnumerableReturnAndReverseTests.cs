using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for IEnumerable&lt;T&gt; → CSharpGenericIterable&lt;T&gt; compatibility:
///  - Reverse() result passed to an IEnumerable&lt;T&gt; parameter must be cast to
///    CSharpGenericIterable (not java.lang.Iterable, which is not assignable).
///  - Returning a ternary of interface-typed IEnumerable&lt;T&gt; values (whose static type is the
///    IEnumerable&lt;T&gt; interface itself) from an IEnumerable&lt;T&gt;-returning member must be wrapped
///    in CSharpGenericIterable.from(...).
///  - Method-syntax GroupBy on an array must use Collectors.groupingBy(..., CSharpList.toCSharpList())
///    so the Map value type is CSharpList&lt;V&gt;, matching IGrouping&lt;K,V&gt; → Map.Entry&lt;K, CSharpList&lt;V&gt;&gt;.
/// </summary>
public class IEnumerableReturnAndReverseTests
{
    private readonly ITestOutputHelper _out;
    public IEnumerableReturnAndReverseTests(ITestOutputHelper output) { _out = output; }

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
