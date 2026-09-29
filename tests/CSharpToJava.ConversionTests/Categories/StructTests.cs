using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# structs (value types) to Java classes implementing Cloneable.
/// </summary>
public class StructTests : ConversionTestBase
{


    [Fact]
    public void StructWithConstructor_ConvertsToJava()
    {
        var result = Convert("struct Point { public int X; public int Y; public Point(int x, int y) { X = x; Y = y; } }");
        AssertConversion(result, "public Point(int x, int y) {", "X = x;", "Y = y;");
    }

    [Fact]
    public void StructWithMethod_ConvertsToJava()
    {
        var result = Convert("struct Counter { public int Value; public void Inc() { Value++; } }");
        AssertConversion(result, "public void inc() {", "Value++;");
    }

    [Fact]
    public void StructFieldInitializer_ConvertsToJava()
    {
        var result = Convert("struct Config { public int Timeout; public Config() { Timeout = 30; } }");
        AssertConversion(result, "public Config() {", "Timeout = 30;");
    }

    [Fact]
    public void StructWithProperties_ConvertsToGettersSetters()
    {
        var result = Convert("struct Person { public string Name { get; set; } public int Age { get; set; } }");
        AssertConversion(result, "public String getName()", "public void setName(String value)", "public int getAge()", "public void setAge(int value)");
    }

    [Fact]
    public void StructUsedAsField_ConvertsToJava()
    {
        var result = Convert("struct Point { public int X; } class C { public Point Origin; }");
        AssertConversion(result, "public Point Origin = new Point();");
    }

    [Fact]
    public void StructWithStaticMethod_ConvertsToJava()
    {
        var result = Convert("struct MathUtil { public static int Square(int x) { return x * x; } }");
        AssertConversion(result, "public static int square(int x) {", "return x * x;");
    }



    [Fact]
    public void StructEquals_ConvertsToJava()
    {
        var result = Convert("struct Pair { public int A; public int B; public override string ToString() { return A + B.ToString(); } }");
        AssertConversion(result, "public String toString() {");
    }



    [Fact]
    public void StructWithBoolField_ConvertsToBoolean()
    {
        var result = Convert("struct Flag { public bool On; }");
        AssertConversion(result, "public boolean On;");
    }

    [Fact]
    public void StructWithDoubleField_ConvertsToJava()
    {
        var result = Convert("struct Measurement { public double Value; }");
        AssertConversion(result, "public double Value;");
    }



    [Fact]
    public void StructWithConst_ConvertsToStaticFinal()
    {
        var result = Convert("struct Constants { public const int Max = 255; }");
        AssertConversion(result, "public static final int Max = 255;");
    }





    [Fact]
    public void StructWithStringField_ConvertsToString()
    {
        var result = Convert("struct Label { public string Text; }");
        AssertConversion(result, "public String Text;");
    }

    [Fact]
    public void StructWithArrayField_ConvertsToJava()
    {
        var result = Convert("struct Buffer { public byte[] Data; }");
        AssertConversion(result, "public byte[] Data;");
    }

    [Fact]
    public void StructWithMultipleConstructors()
    {
        var result = Convert("struct Point { public int X; public Point() { } public Point(int x) { X = x; } }");
        AssertConversion(result, "public Point() {", "public Point(int x) {", "X = x;");
    }

    [Fact]
    public void StructWithIndexer()
    {
        var result = Convert("struct Vec { private int[] d; public int this[int i] { get { return d[i]; } } }");
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        AssertJavaContains(result, "public int get(int i) {");
    }
}
