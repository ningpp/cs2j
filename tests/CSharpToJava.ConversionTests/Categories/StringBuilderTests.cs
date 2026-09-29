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

}
