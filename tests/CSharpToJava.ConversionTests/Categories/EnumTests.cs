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

    [Fact]
    public void FlagsEnum_EmitsValueClassWithFromValue()
    {
        var result = Convert(
            "[System.Flags] enum Perm { None = 0, Read = 1, Write = 2, All = 7 } class C { public Perm P; }");

        AssertSuccess(result);
        // Undeclared flag combinations cannot be represented by a Java enum, so
        // value enums are emitted as an immutable value class.
        AssertJavaContains(result, "final class Perm");
        AssertJavaContains(result, "new Perm(\"None\", 0, 0)");
        AssertJavaContains(result, "new Perm(\"All\", 3, 7)");
        AssertJavaContains(result, "public int getValue()");
        AssertJavaContains(result, "public static Perm fromValue(int value)");
        AssertJavaContains(result, "return new Perm(String.valueOf(value), -1, value);");
    }

    [Fact]
    public void ValueEnumSwitch_CompoundFlagsLabel_UsesNumericSwitch()
    {
        // `A | B` is a constant in C# but a runtime expression in Java, so the
        // switch must be driven on getValue() with integer case labels.
        var result = Convert(@"
[System.Flags] enum Parts { Scheme = 1, Host = 2, Port = 4, All = 7 }
class C {
    public int F(Parts c) {
        switch (c) {
            case Parts.Scheme | Parts.Host: return 1;
            case Parts.All: return 2;
            default: return 0;
        }
    }
}");

        AssertSuccess(result);
        AssertJavaContains(result, "switch ((c).getValue())");
        AssertJavaContains(result, "case 3:");
        AssertJavaContains(result, "case 7:");
        AssertJavaDoesNotContain(result, "case (Parts.fromValue");
    }
}
