using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for C# System.Type equality comparisons.
/// Java splits bool into boolean.class / Boolean.class, so Type equality must
/// normalize primitive/wrapper pairs instead of using Java reference ==.
/// </summary>
public class TypeEqualityTests
{
    [Fact]
    public void SystemTypeEquality_UsesIsSameType_NotReferenceEquality()
    {
        var result = Convert(@"
public class BoolConverter
{
    public bool CanConvert(System.Type typeToConvert)
    {
        return typeToConvert == typeof(bool);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("TypeHelper.isSameType(typeToConvert, Boolean.class)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("typeToConvert == Boolean.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemTypeInequality_UsesNegatedIsSameType()
    {
        var result = Convert(@"
public class BoolConverter
{
    public bool CanConvert(System.Type typeToConvert)
    {
        return typeToConvert != typeof(bool);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("!(TypeHelper.isSameType(typeToConvert, Boolean.class))", code, StringComparison.Ordinal);
        Assert.DoesNotContain("typeToConvert != Boolean.class", code, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericTypeParameterEquality_NormalizesWrapperType()
    {
        var result = Convert(@"
public class BoolConverter<T>
{
    public bool CanConvert(System.Type typeToConvert)
    {
        return typeToConvert == typeof(T);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        Assert.Contains("TypeHelper.isSameType(typeToConvert, TypeHelper.toWrapperType(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("typeToConvert == TypeHelper.toWrapperType(", code, StringComparison.Ordinal);
    }

    private static Core.Pipeline.ConversionResult Convert(string sourceCode)
    {
        var pipeline = new Core.Pipeline.ConversionPipeline();
        return pipeline.Convert(new Core.Pipeline.ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new Core.Context.ConversionOptions(),
        });
    }
}
