using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# System.Text.StringBuilder usage to Java StringBuilder (via StringHelper compat methods).
/// </summary>
public class StringBuilderTests : ConversionTestBase
{
    [Fact]
    public void StringBuilderReplace_ConvertsToReplace()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Replace(\"a\", \"b\"); } }");
        AssertConversion(result, "sb.replace(\"a\", \"b\");");
    }

    [Fact]
    public void StringBuilderToString_ConvertsToToString()
    {
        var result = Convert("class C { public string M() { var sb = new System.Text.StringBuilder(); return sb.ToString(); } }");
        AssertConversion(result, "return sb.toString();");
    }

    [Fact]
    public void StringBuilderSetLength_ConvertsToSetLength()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Length = 0; } }");
        AssertConversion(result, "sb.setLength(0);");
    }

    [Fact]
    public void StringBuilderSetLength_NonZero_ConvertsToSetLength()
    {
        // Length's read-side mapping is field-style (length), which is getter-only;
        // the explicit set_Length mapping must win (regression: sb.length(61)).
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Length = 61; } }");
        AssertConversion(result, "sb.setLength(61);");
        AssertJavaDoesNotContain(result, "sb.length(61)");
    }

    [Fact]
    public void StringBuilderCompoundSetLength_ConvertsToSetLengthWithLengthGetter()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Length += 9; } }");
        AssertConversion(result, "sb.setLength(sb.length() + 9);");
    }

}
