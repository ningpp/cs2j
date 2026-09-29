using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# enums. Plain enums become Java enums with an int value
/// backing field; [Flags] enums become classes with int constants plus bitwise helpers.
/// </summary>
public class EnumTests : ConversionTestBase
{








    [Fact]
    public void EnumUsedAsParameter_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public void Set(Color c) { } }");
        AssertConversion(result, "public void set(Color c) {");
    }

    [Fact]
    public void EnumComparison_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public bool M(Color a, Color b) { return a == b; } }");
        AssertConversion(result, "return a == b;");
    }

    [Fact]
    public void EnumSwitch_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public int M(Color c) { switch (c) { case Color.Red: return 1; default: return 0; } } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "switch");
    }

    [Fact]
    public void EnumInForeachOverValues()
    {
        var result = Convert("enum Color { Red, Green, Blue } class C { public int Count() { int n = 0; foreach (var c in System.Enum.GetValues(typeof(Color))) { n++; } return n; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void EnumToString_ConvertsToName()
    {
        var result = Convert("enum Color { Red, Green } class C { public string M(Color c) { return c.ToString(); } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
    }

    [Fact]
    public void EnumWithMethod_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public void Use() { var c = Color.Red; } }");
        AssertConversion(result, "Color.Red");
    }

    [Fact]
    public void InternalEnum_ConvertsToPackagePrivate()
    {
        var result = Convert("internal enum Level { Low, High }");
        AssertConversion(result, "enum Level {");
    }

    [Fact]
    public void EnumFieldAssignment_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public Color C = Color.Green; }");
        AssertConversion(result, "public Color C = Color.Green;");
    }

    [Fact]
    public void EnumAsReturnType_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public Color Pick() { return Color.Red; } }");
        AssertConversion(result, "public Color pick() {", "return Color.Red;");
    }

    [Fact]
    public void EnumWithUnderscoreNames_ConvertsToJava()
    {
        var result = Convert("enum Http { Ok_200, NotFound_404 }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "enum Http");
    }

    [Fact]
    public void MultipleEnums_ConvertsToJava()
    {
        var result = Convert("enum A { X, Y } enum B { P, Q }");
        AssertConversion(result, "enum A {", "enum B {");
    }
}
