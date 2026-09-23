using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericTypeConversionTests
{

    [Fact]
    public void TypeOf_PrimitiveType_ProducesWrapperClass()
    {
        var result = Convert(@"
class Test {
    void M() { Type t = typeof(int); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        // typeof(int) → Integer.class because Java's int.class creates primitive arrays
        Assert.Contains("Integer.class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_PrimitiveArray_ProducesPrimitiveArrayClass()
    {
        var result = Convert(@"
class Test {
    void M() { Type t = typeof(byte[]); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("byte[].class", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_FuncWithPrimitiveArgs_ProducesRawClass()
    {
        var result = Convert(@"
using System;
class Test {
    void M() { Type t = typeof(Func<int, string>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("Function.class", result.GeneratedCode);
        Assert.DoesNotContain("Function<", result.GeneratedCode);
    }

    [Fact]
    public void TypeOf_ActionWithArrayArg_ProducesRawClass()
    {
        var result = Convert(@"
using System;
class Test {
    void M() { Type t = typeof(Action<byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var code = result.GeneratedCode!;
        Assert.Contains("Consumer.class", code);
        Assert.DoesNotContain("Consumer<", code);
        Assert.DoesNotContain("[]>.class", code);
    }

    [Fact]
    public void InstanceOf_Primitive_ProducesBoxedInstanceof()
    {
        var result = Convert(@"
class Test {
    void M(object obj) { bool b = obj is int; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof Integer", result.GeneratedCode);
    }

    [Fact]
    public void InstanceOf_PrimitiveArray_ProducesPrimitiveArrayInstanceof()
    {
        var result = Convert(@"
class Test {
    void M(object obj) { bool b = obj is byte[]; }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("instanceof byte[]", result.GeneratedCode);
    }

    // ─── default() with generic types ───

    [Fact]
    public void Default_GenericType_ProducesNull()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { var x = default(IList<string>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("null", result.GeneratedCode);
    }

    [Fact]
    public void Default_GenericWithArrayArg_ProducesNull()
    {
        var result = Convert(@"
using System.Collections.Generic;
class Test {
    void M() { var x = default(IList<byte[]>); }
}");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Contains("null", result.GeneratedCode);
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
