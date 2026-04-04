using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ helper methods use Iterable&lt;T&gt; for non-array collection parameters
/// rather than the concrete collection type (avoids type conflicts like java.util.Set vs custom Set).
/// </summary>
public class LinqHelperIterableParameterTests
{
    private readonly ITestOutputHelper _out;
    public LinqHelperIterableParameterTests(ITestOutputHelper output) { _out = output; }

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
    public void DictionaryKeys_LinqHelper_UsesIterableParameter()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    Dictionary<string, int> dict = new Dictionary<string, int>();
    string[] GetKeys() {
        return dict.Keys.Select(k => k.ToUpper()).ToArray();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Parameter should be Iterable<String>, not KeyCollection or Set<String>
        Assert.Contains("Iterable<String>", code);
        Assert.DoesNotContain("KeyCollection", code);
    }

    [Fact]
    public void List_LinqHelper_KeepsConcreteTypeForIndexedLoop()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;
class Sample {
    List<double> values = new List<double>();
    double[] GetDoubled() {
        return values.Select(v => v * 2).ToArray();
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // List<T> source should keep concrete type (ArrayList) for indexed loop
        Assert.Contains("ArrayList<Double>", code);
        // Should NOT downgrade to Iterable (indexed loop needs .size() and .get())
        Assert.DoesNotContain("Iterable<Double>", code);
    }
}
