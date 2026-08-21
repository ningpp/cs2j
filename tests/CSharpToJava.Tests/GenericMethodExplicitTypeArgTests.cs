using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# generic method calls with explicit type arguments
/// (e.g. LoadJson&lt;BugsReference&gt;("path")) correctly insert the
/// Class&lt;T&gt; token argument in the Java output.
/// </summary>
public class GenericMethodExplicitTypeArgTests
{
    private readonly ITestOutputHelper _out;

    public GenericMethodExplicitTypeArgTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void GenericMethodCall_WithExplicitTypeArg_InsertsClassToken()
    {
        // Mirrors E:\agl-master\GraphLayout\...\RoutingTestSupport.cs:
        //   public static T LoadJson<T>(params string[] pathSegments) { ... }
        // Called as:
        //   var x = RoutingTestSupport.LoadJson<BugsReference>("Agl", "Routing");
        var result = Convert("""
using System;

class BugsReference {}

class RoutingTestSupport
{
    public static T LoadJson<T>(params string[] pathSegments)
    {
        return default(T);
    }
}

class Test
{
    static void Run()
    {
        var x = RoutingTestSupport.LoadJson<BugsReference>("Agl", "Routing");
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The call site must include the Class<T> token (.class literal).
        Assert.Contains("BugsReference.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericMethodCall_MemberAccess_NoDefaultT_StillInsertsClassToken()
    {
        // Mirrors the real LoadJson<T> in RoutingTestSupport.cs which does NOT
        // use default(T) in its body — it delegates T to another generic call:
        //   public static T LoadJson<T>(params string[] pathSegments) {
        //       return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        //   }
        // The method definition gains a Class<T> parameter during conversion
        // (because Deserialize<T> needs Class<T>), but the call site may be
        // converted before the definition is registered.
        var result = Convert("""
using System;

class BugsReference {}

class JsonSerializer
{
    public static T Deserialize<T>(string json)
    {
        return default(T);
    }
}

class RoutingTestSupport
{
    public static T LoadJson<T>(params string[] pathSegments)
    {
        return JsonSerializer.Deserialize<T>("dummy");
    }
}

class Test
{
    static void Run()
    {
        var x = RoutingTestSupport.LoadJson<BugsReference>("Agl", "Routing");
    }
}
""");

        _out.WriteLine(result.GeneratedCode ?? "FAILED");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The call site must include the Class<T> token (.class literal).
        Assert.Contains("BugsReference.class", code, StringComparison.Ordinal);
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
