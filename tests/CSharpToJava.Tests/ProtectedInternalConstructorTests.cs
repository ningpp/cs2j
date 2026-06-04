using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ProtectedInternalConstructorTests
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
    public void ProtectedInternalConstructor_GeneratesProtectedOnly()
    {
        var result = Convert(@"
public class Demo
{
    protected internal Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("protected Demo(String version)", result.GeneratedCode);
        Assert.DoesNotContain("public protected", result.GeneratedCode);
    }

    [Fact]
    public void ProtectedInternalConstructor_IncludesComment()
    {
        var result = Convert(@"
public class Demo
{
    protected internal Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("protected internal", result.GeneratedCode);
    }

    [Fact]
    public void ProtectedInternalConstructor_WithParameters_GeneratesProtectedOnly()
    {
        var result = Convert(@"
public class Foo
{
    protected internal Foo(int x, string y) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("protected Foo(int x, String y)", result.GeneratedCode);
        Assert.DoesNotContain("public protected", result.GeneratedCode);
    }

    [Fact]
    public void InternalConstructor_GeneratesPublic()
    {
        var result = Convert(@"
public class Demo
{
    internal Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public Demo(String version)", result.GeneratedCode);
    }

    [Fact]
    public void ProtectedConstructor_GeneratesProtected()
    {
        var result = Convert(@"
public class Demo
{
    protected Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("protected Demo(String version)", result.GeneratedCode);
    }

    [Fact]
    public void PublicConstructor_GeneratesPublic()
    {
        var result = Convert(@"
public class Demo
{
    public Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public Demo(String version)", result.GeneratedCode);
    }

    [Fact]
    public void PrivateConstructor_GeneratesPrivate()
    {
        var result = Convert(@"
public class Demo
{
    private Demo(string version) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("private Demo(String version)", result.GeneratedCode);
    }
}
