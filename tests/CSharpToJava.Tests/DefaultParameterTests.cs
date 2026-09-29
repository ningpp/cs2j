using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DefaultParameterTests
{
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

    [Fact]
    public void Constructor_OneDefaultParam_GeneratesOverload()
    {
        var result = Convert(@"
public class MyClass
{
    public MyClass(int a, int b = 10)
    {
    }
}
");

        Assert.True(result.Success);
        Assert.Contains("MyClass(int a)", result.GeneratedCode);
        Assert.Contains("this(a, 10);", result.GeneratedCode);
        Assert.Contains("MyClass(int a, int b)", result.GeneratedCode);
    }

    [Fact]
    public void Constructor_MultipleDefaultParams_GeneratesMultipleOverloads()
    {
        var result = Convert(@"
public class Foo
{
    public Foo(string name = ""default"", int count = 0, bool flag = true)
    {
    }
}
");

        Assert.True(result.Success);
        Assert.Contains("Foo(String name, int count, boolean flag)", result.GeneratedCode);
        Assert.Contains("Foo()", result.GeneratedCode);
        Assert.Contains("this(\"default\", 0, true);", result.GeneratedCode);
        Assert.Contains("Foo(String name)", result.GeneratedCode);
        Assert.Contains("this(name, 0, true);", result.GeneratedCode);
        Assert.Contains("Foo(String name, int count)", result.GeneratedCode);
        Assert.Contains("this(name, count, true);", result.GeneratedCode);
    }

    [Fact]
    public void AbstractMethod_DefaultParams_Skipped()
    {
        var result = Convert(@"
public abstract class Base
{
    public abstract void Process(int x, int y = 10);
}
");

        Assert.True(result.Success);
        Assert.Contains("abstract void process(int x, int y)", result.GeneratedCode);
        int firstIdx = result.GeneratedCode.IndexOf("void process(int x, int y)");
        int lastIdx = result.GeneratedCode.LastIndexOf("void process(int x, int y)");
        Assert.Equal(firstIdx, lastIdx);
    }

    [Fact]
    public void Constructor_NoDefaultParams_NoOverloads()
    {
        var result = Convert(@"
public class Simple
{
    public Simple(int a, int b)
    {
    }
}
");

        Assert.True(result.Success);
        Assert.Contains("Simple(int a, int b)", result.GeneratedCode);
        Assert.DoesNotContain("this(", result.GeneratedCode);
    }
}
