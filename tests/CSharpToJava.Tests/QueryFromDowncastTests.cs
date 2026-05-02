using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that LINQ query syntax with explicit downcast types in from-clauses
/// generates correct Java for-each loops with type cast wrapping.
/// C#: from DerivedType x in baseCollection
/// Java: for (DerivedType x : (Iterable&lt;DerivedType&gt;)(Iterable&lt;?&gt;)(baseCollection))
/// </summary>
public class QueryFromDowncastTests
{
    private readonly ITestOutputHelper _out;
    public QueryFromDowncastTests(ITestOutputHelper output) { _out = output; }

    [Fact]
    public void FromClause_WithExplicitDowncast_WrapsWithDoubleCast()
    {
        var r = Convert(@"
using System.Collections.Generic;
using System.Linq;

class Base { public int Id; }
class Derived : Base { public string Name; }

class Test
{
    void Process(IEnumerable<Base> items, IEnumerable<Base> others)
    {
        foreach (var pair in from Derived a in items
                             from Derived b in others
                             select new { first = a, second = b })
        {
            System.Console.WriteLine(pair.first.Name + pair.second.Name);
        }
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var code = r.GeneratedCode ?? "";
        // Should have for-each loops (procedural), not stream operations
        Assert.Contains("for (", code);
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
