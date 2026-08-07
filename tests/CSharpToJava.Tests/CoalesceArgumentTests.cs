using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that the "strip trailing CultureInfo from Parse" optimization applies ONLY to
/// recognized numeric parse receivers (int.Parse, double.Parse, ...), not to
/// arbitrary methods named Parse. Reproduces Regex.java:[128,37]
/// "无法将类 RegexParser 中的方法 parse 应用到给定类型" — the converter saw the
/// method name 'Parse' plus a trailing CultureInfo argument and stripped it, but
/// RegexParser.Parse(pattern, options, culture) genuinely needs the culture.
/// </summary>
public class CoalesceArgumentTests
{
    [Fact]
    public void NonNumericParse_KeepsCultureInfoArgument()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Globalization;
public class RegexTree { }
public class RegexParser
{
    public static RegexTree Parse(string pattern, int options, CultureInfo culture) => new RegexTree();
}
public class C
{
    public void M(string pattern, int options, CultureInfo culture)
    {
        RegexTree t = RegexParser.Parse(pattern, options, culture);
    }
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // RegexParser.Parse is not a numeric parse; the CultureInfo argument must survive.
        Assert.Contains("parse(pattern, options, culture)", code);
    }
}
