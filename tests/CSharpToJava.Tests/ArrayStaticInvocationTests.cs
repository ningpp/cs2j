using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayStaticInvocationTests
{
    [Fact]
    public void ArraySort_WithIndexAndLength_UsesArraysSortRange()
    {
        var result = Convert(@"
using System;

class Test {
    void M(int[] values) {
        Array.Sort(values, 1, 2);
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("Arrays.sort(values, 1, 1 + 2)", result.GeneratedCode);
        Assert.DoesNotContain("Object.sort", result.GeneratedCode);
    }

    [Fact]
    public void SystemArrayParameter_MapsToCSharpArray()
    {
        var result = Convert(@"
using System;

class Test {
    int Remaining(Array array, int index, int count) {
        if (array.Length - index < count) {
            return -1;
        }
        return array.Length;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("import io.github.ningpp.compat.CSharpArray;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("int remaining(CSharpArray array, int index, int count)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("array.getLength() - index < count", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return array.getLength();", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Object array", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("java.lang.reflect.Array.getLength(array)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("array.length", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcreteArrayArgument_ToSystemArrayParameter_WrapsInCSharpArray()
    {
        var result = Convert(@"
using System;

class Test {
    int Len(Array array) => array.Length;

    int M(int[] values) {
        return Len(values);
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("int len(CSharpArray array)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return len(CSharpArray.of(values));", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemArrayCast_ToConcreteArray_UnwrapsCSharpArray()
    {
        var result = Convert(@"
using System;

class Test {
    private byte[] _buffer;

    void SetNextOutputBuffer(Array buffer) {
        _buffer = (byte[])buffer;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("void setNextOutputBuffer(CSharpArray buffer)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("_buffer = buffer.as(byte[].class);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_buffer = (byte[])(buffer);", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ArrayCreateInstance_ForSystemArrayReturn_WrapsInCSharpArray()
    {
        var result = Convert(@"
using System;

class Test {
    Array Make(Type type, int count, object value) {
        Array ret = Array.CreateInstance(type, count);
        ret.SetValue(value, 0);
        return ret;
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        Assert.Contains("CSharpArray ret = CSharpArray.of(java.lang.reflect.Array.newInstance(type, count));", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpArray ret = java.lang.reflect.Array.newInstance(type, count);", result.GeneratedCode, StringComparison.Ordinal);
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
