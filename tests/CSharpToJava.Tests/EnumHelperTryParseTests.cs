using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumHelperTryParseTests
{
    [Fact]
    public void EnumTryParse_OutVar_GeneratesEnumHelperTryParseWithClassArg()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public bool TryParse(string s)
    {
        Color result;
        return System.Enum.TryParse(s, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Color.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Enum.TryParse", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumTryParse_IgnoreCase_OutVar_GeneratesEnumHelperTryParseWithClassArg()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public bool TryParse(string s)
    {
        Color result;
        return System.Enum.TryParse(s, true, out result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Color.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("true", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumTryParse_OutVarDeclaration_GeneratesHolderWithClassArg()
    {
        var result = Convert(@"
public enum Status { Open, Closed }

public class Sample
{
    public bool TryParse(string s)
    {
        return System.Enum.TryParse<Status>(s, out var result);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Status.class", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("ObjectHolder", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumParse_GeneratesEnumHelperParse()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public Color Parse(string s)
    {
        return (Color)System.Enum.Parse(typeof(Color), s);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.parse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Enum.Parse", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumParse_IgnoreCase_GeneratesEnumHelperParse()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public Color Parse(string s)
    {
        return (Color)System.Enum.Parse(typeof(Color), s, true);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.parse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("true", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumGetValues_GeneratesEnumHelperGetValues()
    {
        var result = Convert(@"
public enum Color { Red, Green, Blue }

public class Sample
{
    public void PrintAll()
    {
        var values = System.Enum.GetValues(typeof(Color));
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.getValues(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Enum.GetValues", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumTryParse_UsingSystemEnum_GeneratesEnumHelperTryParseWithClassArg()
    {
        var result = Convert(@"
using System;

public enum Direction { North, South, East, West }

public class Sample
{
    public bool TryParse(string s)
    {
        Direction dir;
        return Enum.TryParse(s, out dir);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Direction.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumTryParse_IgnoreCase_UsingSystemEnum_GeneratesEnumHelperTryParseWithClassArg()
    {
        var result = Convert(@"
using System;

public enum Direction { North, South, East, West }

public class Sample
{
    public bool TryParse(string s)
    {
        Direction dir;
        return Enum.TryParse(s, true, out dir);
    }
}");

        Assert.True(result.Success);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("Direction.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void EnumTryParse_IgnoreCase_InferredOutEnum_GeneratesClassArg()
    {
        var result = Convert(@"
using System;
using System.Xml;

public enum GeometryToken { Graph, Unknown }

public class Sample
{
    XmlReader XmlReader;

    public GeometryToken NameToToken()
    {
        GeometryToken token;
        if (Enum.TryParse(XmlReader.Name, true, out token))
            return token;
        return GeometryToken.Unknown;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("EnumHelper.tryParse(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Matches(
            @"EnumHelper\.tryParse\([^;\r\n]*,\s*true,\s*[^;\r\n]*,\s*GeometryToken\.class\)",
            result.GeneratedCode);
        Assert.DoesNotMatch(
            @"EnumHelper\.tryParse\([^;\r\n]*,\s*true,\s*[^;\r\n]*Holder\d+\)",
            result.GeneratedCode);
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
