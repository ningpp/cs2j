using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

public class GenericArrayTypeParameterTests
{
    [Fact]
    public void MethodCreatingTypeParameterArray_AddsRuntimeClassParameter()
    {
        var result = Convert("""
class Demo<T> {
    public T[] Repeat(T value, int count)
    {
        var result = new T[count];

        for (int i = 0; i < count; i++)
        {
            result[i] = value;
        }

        return result;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public T[] repeat(T value, int count, Class<T> clazz)", code);
        Assert.Contains("(T[]) java.lang.reflect.Array.newInstance(clazz, count)", code);
        Assert.DoesNotContain("new Object[count]", code);
    }

    [Fact]
    public void CallToMethodCreatingTypeParameterArray_AddsClassLiteralArgument()
    {
        var result = Convert("""
class Demo<T> {
    public T[] Repeat(T value, int count)
    {
        return new T[count];
    }
}

class UseDemo {
    public string[] Run()
    {
        return new Demo<string>().Repeat("x", 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("repeat(\"x\", 2, String.class)", code);
    }

    [Fact]
    public void GenericCallerForwardsRuntimeClassParameter()
    {
        var result = Convert("""
class Demo<T> {
    public T[] Repeat(T value, int count)
    {
        return new T[count];
    }

    public T[] Twice(T value)
    {
        return Repeat(value, 2);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("public T[] twice(T value, Class<T> clazz)", code);
        Assert.Contains("repeat(value, 2, clazz)", code);
        Assert.DoesNotContain("T.class", code);
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
