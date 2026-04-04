using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// When a Dictionary is passed as an argument to a LINQ helper method whose parameter
/// is Iterable&lt;Map.Entry&gt;, the call site must use .entrySet() because Java Map
/// does not implement Iterable&lt;Map.Entry&gt;.
/// </summary>
public class DictionaryLinqEntrySetTests
{
    private readonly ITestOutputHelper _out;
    public DictionaryLinqEntrySetTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions { PreferStreamApi = false },
        });
    }

    [Fact]
    public void Dictionary_LinqHelper_AddsEntrySetAtCallSite()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    Dictionary<int, double> fixedVars = new Dictionary<int, double>();
    List<int> GetMovedKeys() {
        return fixedVars.Where(kv => kv.Value > 0.5).Select(kv => kv.Key).ToList();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // The call site should use .entrySet() to pass dictionary to Iterable<Map.Entry> param
        Assert.Contains(".entrySet()", code);
    }
}
