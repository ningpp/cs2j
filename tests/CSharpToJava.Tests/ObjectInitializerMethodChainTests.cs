using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that an object initializer used as the receiver of a method call chain
/// is extracted exactly once. The Count/Any probing path in
/// InvocationExpressionTransformer previously transformed the receiver
/// unconditionally, leaving the discarded probe's side-effect pre-statements
/// queued, so the fall-through path transformed the same receiver a second time
/// and the initializer statements were emitted twice:
///
///   new App { Args = args }.Run();
///
/// was converted to
///
///   var _obj1 = new App(); _obj1.setArgs(args);
///   var _obj2 = new App(); _obj2.setArgs(args);
///   _obj2.run();
/// </summary>
public class ObjectInitializerMethodChainTests
{
    [Fact]
    public void ObjectInitializerReceiverOfMethodCall_ShouldInstantiateOnce()
    {
        var result = Convert("""
using System;

class App
{
    public string[] Args { get; set; }
    public void Run() { }
}

class Program
{
    static void Main(string[] args)
    {
        new App { Args = args }.Run();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;

        var newAppCount = CountOccurrences(code, "new App()");
        Assert.Equal(1, newAppCount);

        var setArgsCount = CountOccurrences(code, "setArgs(args)");
        Assert.Equal(1, setArgsCount);

        Assert.Contains(".run()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EntryPointMain_ShouldBePublicStatic()
    {
        var result = Convert("""
using System;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine(args.Length);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains("public static void main(String[] args)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void NestedObjectInitializer_ShouldEmitGetterSetterChain()
    {
        var result = Convert("""
using System;

enum EdgeRoutingMode
{
    SugiyamaSplines,
    StraightLine,
}

class EdgeRoutingSettings
{
    public EdgeRoutingMode EdgeRoutingMode { get; set; }
}

class LayoutSettings
{
    public EdgeRoutingSettings EdgeRoutingSettings { get; } = new EdgeRoutingSettings();
}

class Program
{
    static void Main()
    {
        var settings = new LayoutSettings
        {
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.StraightLine }
        };
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains(".getEdgeRoutingSettings().setEdgeRoutingMode(EdgeRoutingMode.StraightLine);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void DeeplyNestedObjectInitializer_ShouldEmitChainedGettersAndSetter()
    {
        var result = Convert("""
using System;

class Inner
{
    public int Deep { get; set; }
}

class Outer
{
    public Inner Inner { get; } = new Inner();
}

class Program
{
    static void Main()
    {
        var value = new Outer
        {
            Inner = { Deep = 5 }
        };
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        var code = result.GeneratedCode;
        Assert.Contains(".getInner().setDeep(5);", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

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
