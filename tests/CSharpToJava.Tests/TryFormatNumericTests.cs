using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class TryFormatNumericTests
{
    [Fact]
    public void ByteTryFormat_GeneratesMathHelperTryFormatByte()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        byte a = 255;
        char[] buf = new char[10];
        a.TryFormat(buf, out int w, ""X2"", null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatByte(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".TryFormat(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntTryFormat_GeneratesMathHelperTryFormatInt()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        int a = 42;
        char[] buf = new char[20];
        a.TryFormat(buf, out int w, ""D8"", null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatInt(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".TryFormat(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleTryFormat_GeneratesMathHelperTryFormatDouble()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        double a = 3.14;
        char[] buf = new char[30];
        a.TryFormat(buf, out int w, ""F2"", null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatDouble(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".TryFormat(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LongTryFormat_GeneratesMathHelperTryFormatLong()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        long a = 123456789L;
        char[] buf = new char[30];
        a.TryFormat(buf, out int w);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatLong(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain(".TryFormat(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatTryFormat_GeneratesMathHelperTryFormatFloat()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        float a = 2.5f;
        char[] buf = new char[20];
        a.TryFormat(buf, out int w, ""F1"", null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatFloat(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTryFormat_GeneratesMathHelperTryFormatShort()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        short a = 100;
        char[] buf = new char[10];
        a.TryFormat(buf, out int w, null, null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatShort(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryFormat_OutVar_GeneratesIntHolder()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        int a = 42;
        char[] buf = new char[20];
        a.TryFormat(buf, out var w, null, null);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatInt(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("IntHolder", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryFormat_ReturnValueUsedInIf()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        int a = 42;
        char[] buf = new char[20];
        if (!a.TryFormat(buf, out int w, null, null))
            throw new System.Exception(""buffer too small"");
    }
}");

        Assert.True(result.Success);
        Assert.Contains("MathHelper.tryFormatInt(", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void NonNumericTryFormat_NotTransformed()
    {
        var result = Convert(@"
public class Sample
{
    public void Format()
    {
        MyType a = new MyType();
        char[] buf = new char[10];
        a.TryFormat(buf, out int w);
    }
}

public class MyType
{
    public bool TryFormat(char[] buf, out int w) { w = 0; return false; }
}");

        Assert.True(result.Success);
        Assert.DoesNotContain("MathHelper.tryFormat", result.GeneratedCode, StringComparison.Ordinal);
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
