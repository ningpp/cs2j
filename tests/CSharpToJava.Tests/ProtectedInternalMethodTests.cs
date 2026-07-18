using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ProtectedInternalMethodTests
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
    public void ProtectedInternalMethod_GeneratesPublic()
    {
        var result = Convert(@"
public class Demo
{
    protected internal void SetSourceObject(Object source) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public void setSourceObject(Object source)", result.GeneratedCode);
        Assert.DoesNotContain("protected void setSourceObject(Object source)", result.GeneratedCode);
    }

    [Fact]
    public void ProtectedInternalMethod_WithReturnType_GeneratesPublic()
    {
        var result = Convert(@"
public class Demo
{
    protected internal int Compute() => 42;
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public int compute()", result.GeneratedCode);
    }

    [Fact]
    public void ProtectedMethod_StillGeneratesProtected()
    {
        var result = Convert(@"
public class Demo
{
    protected void SetSourceObject(Object source) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("protected void setSourceObject(Object source)", result.GeneratedCode);
    }

    [Fact]
    public void InternalMethod_StillGeneratesPublic()
    {
        var result = Convert(@"
public class Demo
{
    internal void SetSourceObject(Object source) {}
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("public void setSourceObject(Object source)", result.GeneratedCode);
    }
}
