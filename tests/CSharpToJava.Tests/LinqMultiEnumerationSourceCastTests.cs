using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue: a C# collection type that implements BOTH a generic list
/// enumeration (IList&lt;T&gt; / IEnumerable&lt;T&gt;) AND IReadOnlyDictionary&lt;K, V&gt;
/// (e.g. the BCL GroupCollection) loses the IEnumerable&lt;T&gt; enumeration in the
/// Java mapping (CSharpReadOnlyDict iterates KeyValuePair&lt;K,V&gt;).
///
/// When such a collection is the source of a LINQ chain rewritten by the LINQ
/// rewriter (e.g. `.Cast&lt;T&gt;()`), the generated call site wraps the collection
/// in CSharpGenericIterable.from(...) with the WRONG element type
/// (KeyValuePair instead of the dictionary's Value type), so javac fails
/// generic type inference:
///   "incompatible types: inferred type variable T has incompatible bounds"
///   (equality constraints: Group; lower bounds: CSharpKeyValuePair&lt;String,Group&gt;)
///
/// Expected Java output: the collection must be adapted via .getValues()
/// (C# GroupCollection.Values enumerates index 0..Count-1, exactly like its
/// IEnumerable&lt;Group&gt; enumeration), which yields a CSharpReadOnlyCollection
/// assignable to CSharpGenericIterable&lt;Group&gt;.
/// </summary>
public class LinqMultiEnumerationSourceCastTests
{
    private readonly ITestOutputHelper _out;

    public LinqMultiEnumerationSourceCastTests(ITestOutputHelper output) => _out = output;

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
