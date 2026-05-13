using System;
using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class PropertyReceiverMethodCallTests
{
    [Fact]
    public void PropertyReceiver_MethodCallsAndChains_UseGetterReceiver()
    {
        var result = Convert(@"
class Graph {
    public string Name { get; set; }
    public string Build() { return Name; }
}

class Holder {
    public Graph Graph { get; set; }
    public Graph Other { get; set; }

    string A() { return Graph.Build(); }
    string B() { return Other.Build(); }
    string C() { return Graph.Name.ToString(); }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("return getGraph().build();", code, StringComparison.Ordinal);
        Assert.Contains("return getOther().build();", code, StringComparison.Ordinal);
        Assert.Contains("return getGraph().getName().toString();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Graph.build();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Other.build();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Graph.getName().toString();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyReceiver_StaticMemberAccess_KeepsStaticTypeReceiver()
    {
        var result = Convert(@"
class Factory {
    public static Factory Create() { return new Factory(); }
    public string Build() { return ""ok""; }
}

class Holder {
    public Factory Factory { get; set; }

    Factory StaticCreate() { return Factory.Create(); }
    string InstanceBuild() { return Factory.Build(); }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("return Factory.create();", code, StringComparison.Ordinal);
        Assert.Contains("return getFactory().build();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return getFactory().create();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Factory.build();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PropertyReceiver_StaticPropertyAccess_KeepsStaticTypeReceiver()
    {
        var result = Convert(@"
class Clock {
    public static int Tick { get; set; }
    public int Value { get; set; }
}

class Holder {
    public Clock Clock { get; set; }

    int StaticTick() { return Clock.Tick; }
    int InstanceValue() { return Clock.Value; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode!;

        Assert.Contains("return Clock.getTick();", code, StringComparison.Ordinal);
        Assert.Contains("return getClock().getValue();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return getClock().getTick();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Clock.getValue();", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrossFile_PropertyReceiver_MethodCall_UsesGetterReceiver()
    {
        var graphFile = @"
namespace Test.Core {
    public class Graph {
        public string Name { get; set; }
        public string Build() { return Name; }
    }
}";

        var holderFile = @"
using Test.Core;

namespace Test.App {
    public class Holder {
        public Graph Graph { get; set; }
        public Graph Other { get; set; }

        public string A() { return Graph.Build(); }
        public string B() { return Other.Build(); }
        public string C() { return Graph.Name.ToString(); }
    }
}";

        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile { FilePath = "Core/Graph.cs", Content = graphFile },
            new SourceFile { FilePath = "App/Holder.cs", Content = holderFile },
        });

        var holderResult = Assert.Single(results, r => r.FileName == "Holder.java");
        Assert.True(holderResult.Success, string.Join("\n", holderResult.Diagnostics));
        var code = holderResult.GeneratedCode!;

        Assert.Contains("return getGraph().build();", code, StringComparison.Ordinal);
        Assert.Contains("return getOther().build();", code, StringComparison.Ordinal);
        Assert.Contains("return getGraph().getName().toString();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Graph.build();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("return Other.build();", code, StringComparison.Ordinal);
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

    private static ConversionOptions CreateProjectOptions()
        => new()
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
}
