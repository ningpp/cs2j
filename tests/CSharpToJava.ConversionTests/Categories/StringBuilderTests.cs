using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# System.Text.StringBuilder usage to Java StringBuilder (via StringHelper compat methods).
/// </summary>
public class StringBuilderTests : ConversionTestBase
{
    [Fact]
    public void StringBuilderAppend_ConvertsToHelper()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Append(\"a\"); } }");
        AssertConversion(result,
            "var sb = new StringBuilder();",
            "StringHelper.append(sb, \"a\");");
    }

    [Fact]
    public void StringBuilderAppendLine_ConvertsToHelper()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.AppendLine(\"b\"); } }");
        AssertConversion(result, "StringHelper.appendLine(sb, \"b\");");
    }

    [Fact]
    public void StringBuilderAppendLineNoArgs_ConvertsToHelper()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.AppendLine(); } }");
        AssertConversion(result, "StringHelper.appendLine(sb);");
    }

    [Fact]
    public void StringBuilderInsert_ConvertsToHelper()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Insert(0, \"x\"); } }");
        AssertConversion(result, "StringHelper.insert(sb, 0, \"x\");");
    }

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
    public void StringBuilderClear_ConvertsToSetLength()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Clear(); } }");
        AssertConversion(result, "sb.setLength(0);");
    }

    [Fact]
    public void StringBuilderLengthProperty_ConvertsToLength()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); int n = sb.Length; } }");
        AssertConversion(result, "int n = sb.length();");
    }

    [Fact]
    public void StringBuilderSetLength_ConvertsToSetLength()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(); sb.Length = 0; } }");
        AssertConversion(result, "sb.setLength(0);");
    }

    [Fact]
    public void StringBuilderCapacity_ConvertsToCapacity()
    {
        var result = Convert("class C { public void M() { var sb = new System.Text.StringBuilder(10); int c = sb.Capacity; } }");
        AssertConversion(result, "var sb = new StringBuilder(10);", "int c = sb.capacity();");
    }

    [Fact]
    public void StringBuilderFullChain_ConvertsCorrectly()
    {
        var result = Convert("class C { public string M() { var sb = new System.Text.StringBuilder(); sb.Append(\"a\"); sb.AppendLine(\"b\"); sb.Insert(0, \"x\"); sb.Replace(\"a\", \"b\"); return sb.ToString(); } }");
        AssertConversion(result,
            "StringHelper.append(sb, \"a\");",
            "StringHelper.appendLine(sb, \"b\");",
            "StringHelper.insert(sb, 0, \"x\");",
            "sb.replace(\"a\", \"b\");",
            "return sb.toString();");
    }
}
