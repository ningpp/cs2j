using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for correct conversion of string.Empty and String.Empty to Java empty string literal "".
/// </summary>
public class StringEmptyConversionTests
{
    [Fact]
    public void StringKeywordEmpty_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    public string GetDefault()
    {
        return string.Empty;
    }
}");

        Assert.True(result.Success, $"Conversion failed. Generated code:\n{result.GeneratedCode}");
        Assert.Contains("return \"\";", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringIdentifierEmpty_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    public string GetDefault()
    {
        return String.Empty;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("return \"\";", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FullyQualifiedStringEmpty_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    public string GetDefault()
    {
        return System.String.Empty;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("return \"\";", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringEmpty_InTernaryExpression_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    public string GetDefault(string s)
    {
        return s != null ? s : string.Empty;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("\"\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringEmpty_InFieldInitializer_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    private string _name = string.Empty;
}");

        Assert.True(result.Success);
        Assert.Contains("\"\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void StringEmpty_InLocalDeclaration_ConvertsToEmptyStringLiteral()
    {
        var result = Convert(@"
class Sample
{
    public void DoSomething()
    {
        string s = string.Empty;
    }
}");

        Assert.True(result.Success);
        Assert.Contains("\"\"", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("String.Empty", result.GeneratedCode, StringComparison.Ordinal);
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
