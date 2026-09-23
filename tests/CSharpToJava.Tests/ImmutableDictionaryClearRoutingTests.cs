using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C# IImmutableDictionary&lt;K,V&gt;.Clear() returns an empty dictionary, but the compat
/// interface cannot carry a fluent clear(): its implementations also realize
/// CSharpReadOnlyDict (the IReadOnlyDictionary port), whose Collection ancestor owns
/// void clear() — a fluent clear() would clash on erasure. The call must route to the
/// interface's static IImmutableDictionary.clear helper instead of an instance call.
/// </summary>
public class ImmutableDictionaryClearRoutingTests
{

    [Fact]
    public void Clear_OnMutableCollections_KeepsInstanceCall()
    {
        var result = Convert("""
using System.Collections.Generic;

class Demo {
    private System.Collections.Generic.List<string> _list = new System.Collections.Generic.List<string>();
    private Dictionary<string, string> _dict = new Dictionary<string, string>();

    public void Wipe()
    {
        _list.Clear();
        _dict.Clear();
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // The routing is scoped to System.Collections.Immutable — mutable collections
        // keep their normal instance clear() calls.
        Assert.Contains("_list.clear()", code);
        Assert.Contains("_dict.clear()", code);
        Assert.DoesNotContain("IImmutableDictionary.clear(", code);
    }

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
