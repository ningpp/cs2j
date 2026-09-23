using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that a static call written on a C# primitive keyword is rendered through the Java
/// wrapper class. A Java primitive keyword is not a type name, so <c>double.IsNaN(x)</c>
/// produces "&lt;class&gt; expected" in javac; it must become <c>Double.isNaN(x)</c>.
/// Reproduces ArraySortHelper.cs:1170-1172 (<c>double.IsNaN</c> / <c>float.IsNaN</c>).
/// </summary>
public class PrimitiveStaticInvocationTests
{
    [Fact]
    public void DoubleIsNaN_MapsToDoubleWrapper()
    {
        var code = Convert(@"
class Test
{
    bool M(object value)
    {
        return double.IsNaN((double)value) || float.IsNaN((float)value);
    }
}");

        Assert.Contains("Double.isNaN(", code);
        Assert.Contains("Float.isNaN(", code);
        Assert.DoesNotContain("double.isNaN(", code);
        Assert.DoesNotContain("float.isNaN(", code);
    }

    [Fact]
    public void DoubleIsInfinity_MapsToWrapperMethod()
    {
        var code = Convert(@"
class Test
{
    bool M(double d, float f)
    {
        return double.IsInfinity(d) || float.IsInfinity(f);
    }
}");

        Assert.Contains("Double.isInfinite(", code);
        Assert.Contains("Float.isInfinite(", code);
        Assert.DoesNotContain("double.isInfinite(", code);
        Assert.DoesNotContain("float.isInfinite(", code);
    }

    [Fact]
    public void IsPositiveInfinity_ComparesAgainstConstant()
    {
        var code = Convert(@"
class Test
{
    bool M(double d)
    {
        return double.IsPositiveInfinity(d);
    }
}");

        Assert.Contains("Double.POSITIVE_INFINITY", code);
        Assert.DoesNotContain("double.", code);
    }

    [Fact]
    public void IntParse_UsesParseInt()
    {
        var code = Convert(@"
class Test
{
    int M(string s)
    {
        return int.Parse(s);
    }
}");

        Assert.Contains(".parseInt(s)", code);
        Assert.DoesNotContain("int.parseInt(", code);
    }

    [Fact]
    public void CharIsLower_MapsToCharacterIsLowerCase()
    {
        var code = Convert(@"
class Test
{
    bool M(char c)
    {
        return char.IsLower(c);
    }
}");

        Assert.Contains("Character.isLowerCase(c)", code);
    }

    private static string Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        return result.GeneratedCode ?? "";
    }
}
