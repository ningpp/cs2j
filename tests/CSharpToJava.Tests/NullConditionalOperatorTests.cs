using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NullConditionalOperatorTests
{
    [Fact]
    public void NullConditional_ToString_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void WriteValue(object value)
    {
        WriteString(value?.ToString() ?? string.Empty);
    }

    private void WriteString(string s) { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toString()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getToString", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ToString_WithCoalesce_GeneratesCorrectTernary()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(object value)
    {
        var s = value?.ToString() ?? string.Empty;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toString()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_GetHashCode_MapsToHashCode()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(object value)
    {
        var h = value?.GetHashCode() ?? 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.hashCode()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("hashCode()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getGetHashCode", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_PropertyAccess_NoParentheses()
    {
        var result = Convert("""
using System.Collections.Generic;

class TestClass
{
    public void M(List<int> list)
    {
        var count = list?.Count ?? 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("list.size()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("size()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ChainedMethod_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(string value)
    {
        var upper = value?.ToUpper();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.toUpperCase()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toUpperCase()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getToUpper", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ChainedMethodChain_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(string s)
    {
        var lower = s?.Trim()?.ToLower();
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.trim()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("s.trim().toLowerCase()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("trim()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toLowerCase()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ChainedMethodWithCoalesce_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(object obj)
    {
        var result = obj?.ToString()?.ToUpper() ?? "DEFAULT";
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("obj.toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("obj.toString().toUpperCase()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toString()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toUpperCase()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ChainedPropertyAndMethod_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(object obj)
    {
        var len = obj?.ToString()?.Length ?? 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("obj.toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("obj.toString().length()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toString()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("length()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_MethodWithArgs_NoDoubleParentheses()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(string s)
    {
        var sub = s?.Substring(0, 5);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("s.substring(0, 5)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("substring()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ChainedPropertyAccess_WithCoalesce()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(Exception ex)
    {
        var len = ex?.Message?.Length ?? -1;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("ex.getMessage()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ex.getMessage().length()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getMessage()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("length()()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_MultipleCoalesce_Chain()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void M(string a, string b, string c)
    {
        var result = a ?? b ?? c ?? "default";
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("a != null ? a : b != null ? b : c != null ? c : \"default\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NullConditional_ToString_WithCoalesceInMethodCall()
    {
        var result = Convert("""
using System;

class TestClass
{
    public void WriteValue(object value)
    {
        WriteString(value?.ToString() ?? string.Empty);
    }

    private void WriteString(string s) { }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("value.toString()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("toString()()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getToString", result.GeneratedCode, StringComparison.Ordinal);
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
