using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that plain Java List<T> (e.g. from Enumerable.Empty<T>() →
/// Collections.emptyList()) is automatically wrapped via
/// CSharpGenericIterable.from() when the target parameter type is
/// IEnumerable&lt;T&gt; (mapped to CSharpGenericIterable&lt;T&gt;).
/// </summary>
public class CSharpGenericIterableCompatTests
{
    private readonly ITestOutputHelper _out;

    public CSharpGenericIterableCompatTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EnumerableEmpty_PassedToIEnumerableParam_WrapsWithCSharpGenericIterable()
    {
        // Mirrors CdtTest.cs:
        //   AssertValidTriangulation(cdt, points,
        //       Enumerable.Empty<SymmetricTuple<Point>>(), "seed=" + seed);
        // where the third parameter is IEnumerable<SymmetricTuple<Point>>.
        var result = Convert("""
using System;
using System.Collections.Generic;
using System.Linq;

class SymmetricTuple<T> {}

class Test
{
    static void DoWork(IEnumerable<SymmetricTuple<int>> items) {}
    static void Run()
    {
        DoWork(Enumerable.Empty<SymmetricTuple<int>>());
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The call site must wrap the Collections.emptyList() with
        // CSharpGenericIterable.from(...) so it matches the
        // CSharpGenericIterable<SymmetricTuple<Integer>> parameter.
        Assert.Contains("CSharpGenericIterable.from", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_PassedToIListParam_WrapsWithCSharpGenericIList()
    {
        // Mirrors ConstrainedOrderingInternalTest.java line 93:
        //   _obj106.setNodes(nodes);
        // where nodes is Node[] and setNodes takes CSharpGenericIList<Node>
        var result = Convert("""
using System;
using System.Collections.Generic;

class Point {}

class Test
{
    static void DoWork(IList<Point> items) {}
    static void Run()
    {
        DoWork(new Point[] { new Point() });
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The array expression must be wrapped with CSharpGenericIList.from(...)
        Assert.Contains("CSharpGenericIList.from", code, StringComparison.Ordinal);
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
