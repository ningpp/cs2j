using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Nullable&lt;T&gt;.GetValueOrDefault() is converted to a
/// null-coalescing ternary in Java, since Java boxed types lack this method.
/// The LINQ rewriter emits ((items as ICollection&lt;T&gt;)?.Count).GetValueOrDefault()
/// which must produce  (expr != null ? expr : 0)  in Java.
/// </summary>
public class GetValueOrDefaultMappingTests
{
    [Fact]
    public void GetValueOrDefault_NoArgs_TranslatesToNullCoalescing()
    {
        var result = Convert(@"
using System;

public class Demo
{
    public int Foo(int? value)
    {
        return value.GetValueOrDefault();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("getValueOrDefault", result.GeneratedCode);
        Assert.DoesNotContain("GetValueOrDefault", result.GeneratedCode);
        Assert.Contains("!= null ?", result.GeneratedCode);
    }

    [Fact]
    public void GetValueOrDefault_WithArg_TranslatesToNullCoalescingWithDefault()
    {
        var result = Convert(@"
using System;

public class Demo
{
    public int Foo(int? value)
    {
        return value.GetValueOrDefault(42);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("getValueOrDefault", result.GeneratedCode);
        Assert.DoesNotContain("GetValueOrDefault", result.GeneratedCode);
        Assert.Contains("!= null ?", result.GeneratedCode);
        Assert.Contains(": 42", result.GeneratedCode);
    }

    [Fact]
    public void GetValueOrDefault_OnCastExpression_TranslatesCorrectly()
    {
        var result = Convert(@"
using System;
using System.Collections.Generic;

public class Demo
{
    public int CountItems<T>(IEnumerable<T> items)
    {
        return ((items as ICollection<T>)?.Count).GetValueOrDefault();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("getValueOrDefault", result.GeneratedCode);
        Assert.DoesNotContain("GetValueOrDefault", result.GeneratedCode);
        Assert.Contains("!= null ?", result.GeneratedCode);
    }

    [Fact]
    public void GetValueOrDefault_Double_UsesCorrectDefault()
    {
        var result = Convert(@"
using System;

public class Demo
{
    public double Foo(double? value)
    {
        return value.GetValueOrDefault();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("getValueOrDefault", result.GeneratedCode);
        Assert.DoesNotContain("GetValueOrDefault", result.GeneratedCode);
        Assert.Contains("!= null ?", result.GeneratedCode);
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
