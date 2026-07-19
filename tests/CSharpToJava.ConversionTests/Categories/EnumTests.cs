using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# enums. Plain enums become Java enums with an int value
/// backing field; [Flags] enums become classes with int constants plus bitwise helpers.
/// </summary>
public class EnumTests : ConversionTestBase
{
    [Fact]
    public void SimpleEnum_ConvertsToJavaEnum()
    {
        var result = Convert("enum Color { Red, Green, Blue }");
        AssertConversion(result, "public enum Color {", "Red(0),", "Green(1),", "Blue(2),", "_UNMAPPED(-1);");
    }

    [Fact]
    public void EnumWithExplicitValues_ConvertsToJavaEnum()
    {
        var result = Convert("enum Priority { Low = 1, Medium = 2, High = 3 }");
        AssertConversion(result, "Low(1),", "Medium(2),", "High(3),");
    }

    [Fact]
    public void EnumWithValueMethod_ConvertsToGetValue()
    {
        var result = Convert("enum Color { Red, Green }");
        AssertConversion(result, "public int getValue() {");
    }

    [Fact]
    public void EnumWithFromValue_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green }");
        AssertConversion(result, "public static Color fromValue(int v) {");
    }

    [Fact]
    public void FlagsEnum_ConvertsToClassWithIntConstants()
    {
        var result = Convert("[System.Flags] enum Mode { A = 1, B = 2, C = 4 }");
        AssertConversion(result, "public static final int A = 1;", "public static final int B = 2;", "public static final int C = 4;");
    }

    [Fact]
    public void FlagsEnum_HasBitwiseHelpers()
    {
        var result = Convert("[System.Flags] enum Mode { A = 1, B = 2 }");
        AssertConversion(result, "public static int and(int a, int b) {", "public static int or(int a, int b) {", "public static boolean has(int flags, int flag) {");
    }

    [Fact]
    public void EnumUsedAsFieldType_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public Color Favorite; }");
        AssertConversion(result, "public Color Favorite = Color.Red;");
    }

    [Fact]
    public void EnumUsedAsParameter_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public void Set(Color c) { } }");
        AssertConversion(result, "public void set(Color c) {");
    }

    [Fact]
    public void EnumWithExplicitZero_ConvertsToJavaEnum()
    {
        var result = Convert("enum Status { None = 0, Ok = 200 }");
        AssertConversion(result, "None(0),", "Ok(200),");
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
    public void EnumCastFromInt_ConvertsToFromValue()
    {
        var result = Convert("enum Color { Red, Green } class C { public Color M(int x) { return (Color)x; } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "fromValue");
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
    public void EnumWithLongValues_ConvertsToJava()
    {
        var result = Convert("enum Big { A = 1L, B = 2L }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "A(");
    }

    [Fact]
    public void EnumBackedByByte_ConvertsToJava()
    {
        var result = Convert("enum Small : byte { A, B }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "enum Small");
    }

    [Fact]
    public void FlagsEnumWithCombined_ConvertsToClass()
    {
        var result = Convert("[System.Flags] enum Opt { Read = 1, Write = 2, Exec = 4 }");
        AssertConversion(result, "public static final int Read = 1;", "public static final int Write = 2;", "public static final int Exec = 4;");
    }

    [Fact]
    public void EnumFieldAssignment_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public Color C = Color.Green; }");
        AssertConversion(result, "public Color C = Color.Green;");
    }

    [Fact]
    public void EnumInDictionaryKey_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green } class C { public System.Collections.Generic.Dictionary<Color, int> Map = new(); }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "CSharpDictionary");
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
    public void EnumFromValueUnchecked_ConvertsToJava()
    {
        var result = Convert("enum Color { Red, Green }");
        AssertConversion(result, "public static Color fromValueUnchecked(int v) {");
    }

    [Fact]
    public void ConstIntCastFromEnum_InlinesLiteralValue()
    {
        var result = Convert(@"
enum Status { None = 0, Ok = 1 }
class C {
    public const int STATUS_OK = (int)Status.Ok;
    public int M() { return STATUS_OK; }
}");
        AssertConversion(result,
            "public static final int STATUS_OK = 1;",
            "return STATUS_OK;");
        AssertJavaDoesNotContain(result, ".getValue()", "const enum cast should not generate getValue() call");
    }
}
