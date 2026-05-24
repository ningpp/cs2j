using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Diagnostics;
using System.Text;
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

    [Fact]
    public void SharedGenericArrayFactoryInDenseCallGraph_ConvertsWithoutRepeatedRecursiveScans()
    {
        var source = new StringBuilder("""
class Demo<T> {
    public T[] Repeat(T value, int count)
    {
        return new T[count];
    }

""");

        source.AppendLine("""
    public T[] Wrapper0(T value)
    {
        return Repeat(value, 1);
    }

""");

        for (var i = 1; i < 24; i++)
        {
            var calls = string.Join(Environment.NewLine, Enumerable.Range(0, i).Select(j => $"        Wrapper{j}(value);"));
            source.AppendLine($$"""
    public T[] Wrapper{{i}}(T value)
    {
{{calls}}
        return Repeat(value, {{i + 1}});
    }

""");
        }

        source.AppendLine("}");

        var stopwatch = Stopwatch.StartNew();
        var result = Convert(source.ToString());
        stopwatch.Stop();

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Conversion took {stopwatch.ElapsedMilliseconds} ms");
        var code = result.GeneratedCode!;

        Assert.Contains("public T[] repeat(T value, int count, Class<T> clazz)", code);
        Assert.Contains("public T[] wrapper23(T value, Class<T> clazz)", code);
        Assert.Contains("repeat(value, 24, clazz)", code);
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
