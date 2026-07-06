using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of miscellaneous C# built-in members: object.Equals, string interpolation
/// (with/without format), string.Empty, and string.Format.
/// </summary>
public class MiscBuiltinTests : ConversionTestBase
{
    [Fact]
    public void StaticObjectEquals_ConvertsToObjectsEquals()
    {
        var result = Convert("class C { public bool M(object a, object b) { return object.Equals(a, b); } }");
        AssertConversion(result,
            "import java.util.Objects;",
            "return Objects.equals(a, b);");
    }

    [Fact]
    public void StringInterpolationMulti_ConvertsToConcatenation()
    {
        var result = Convert("class C { public string M(int a, int b) { return $\"{a}-{b}\"; } }");
        AssertConversion(result, "return a + \"-\" + b;");
    }

    [Fact]
    public void StringInterpolationFormat_ConvertsToFormatWithTodo()
    {
        var result = Convert("class C { public string M(int x) { return $\"{x:000}\"; } }");
        AssertConversion(result, "return String.format(\"%s /* TODO: format specifier 000 */\", x);");
    }

    [Fact]
    public void StringEmptyStatic_ConvertsToEmptyLiteral()
    {
        var result = Convert("class C { public void M() { var s = string.Empty; } }");
        AssertConversion(result, "var s = \"\";");
    }

    [Fact]
    public void StringFormat_ConvertsToJavaFormat()
    {
        var result = Convert("class C { public string M(int a, int b) { return string.Format(\"{0}-{1}\", a, b); } }");
        AssertConversion(result, "return String.format(\"%1$s-%2$s\", a, b);");
    }

    [Fact]
    public void StringInterpolationNoCSharpResidue()
    {
        var result = Convert("class C { public string M(int a) { return $\"v={a}\"; } }");
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "v=\" + a");
    }

    [Fact]
    public void ObjectEqualsStaticNoCSharpResidue()
    {
        var result = Convert("class C { public bool M(object a, object b) { return object.Equals(a, b); } }");
        AssertJavaDoesNotContain(result, "object.Equals", "C# 'object.Equals' must be rewritten");
    }

    [Fact]
    public void StringFormatWithThreeArgs_ConvertsToJavaFormat()
    {
        var result = Convert("class C { public string M(int a, int b, int c) { return string.Format(\"{0}{1}{2}\", a, b, c); } }");
        AssertConversion(result, "return String.format(\"%1$s%2$s%3$s\", a, b, c);");
    }
}
