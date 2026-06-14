using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;
using Xunit.Abstractions;

namespace CSharpToJava.Tests;

/// <summary>
/// System.Collections.Generic.ValueListBuilder&lt;T&gt; is an internal .NET ref struct.
/// When used in C# source, the converter should map it to the compat
/// io.github.ningpp.compat.ValueListBuilder&lt;T&gt; class and convert
/// Append(T) → append(T) and Length → getLength().
/// </summary>
public class ValueListBuilderMappingTests
{
    private readonly ITestOutputHelper _out;
    public ValueListBuilderMappingTests(ITestOutputHelper output) { _out = output; }

    private ConversionResult Convert(string src)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = src,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }

    [Fact]
    public void ValueListBuilder_MappedToCompatClass()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        ValueListBuilder<int> builder = new ValueListBuilder<int>();
        builder.Append(42);
        int len = builder.Length;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Should map to the compat class, not dotnet.system.Collections.Generic.ValueListBuilder
        Assert.DoesNotContain("dotnet.system.Collections.Generic.ValueListBuilder", code);
        Assert.Contains("ValueListBuilder", code);
        // Should import the compat class
        Assert.Contains("import io.github.ningpp.compat.ValueListBuilder", code);
    }

    [Fact]
    public void ValueListBuilder_AppendConvertedToAppend()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Sample
{
    void M()
    {
        ValueListBuilder<int> builder = new ValueListBuilder<int>();
        builder.Append(1);
        builder.Append(2);
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Append should be converted to append (camelCase)
        Assert.Contains("builder.append(1)", code);
        Assert.Contains("builder.append(2)", code);
    }

    [Fact]
    public void ValueListBuilder_LengthConvertedToGetLength()
    {
        var r = Convert(@"
using System.Collections.Generic;

class Sample
{
    int M()
    {
        ValueListBuilder<int> builder = new ValueListBuilder<int>();
        builder.Append(42);
        return builder.Length;
    }
}");
        _out.WriteLine(r.GeneratedCode ?? "FAILED");
        Assert.True(r.Success);
        var code = r.GeneratedCode ?? "";
        // Length property should be converted to getLength() via getter pattern
        Assert.Contains("builder.getLength()", code);
    }
}
