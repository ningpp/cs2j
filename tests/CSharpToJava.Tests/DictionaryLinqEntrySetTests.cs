using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// CSharpDictionary exposes its entries through CSharpGenericIDictionary, not
/// CSharpGenericIterable. A dictionary passed to a generated LINQ helper is
/// bridged with CSharpGenericIterable.fromEntries(...); no .entrySet() is
/// emitted at the call site.
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
        // CSharpDictionary is not a CSharpGenericIterable, so it reaches the
        // procedural LINQ helper through the fromEntries bridge, not .entrySet()
        Assert.Contains("getMovedKeys_ProceduralLinq1(CSharpGenericIterable.fromEntries(fixedVars))", code);
        Assert.DoesNotContain(".entrySet()", code);
    }
}
