using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Comprehensive tests for ref/out/in parameter conversion across various types.
/// Verifies that the C#-to-Java converter generates the correct holder types and value access patterns.
/// </summary>
public class RefOutInComprehensiveTests : ConversionTestBase
{
    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefDecimal_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref decimal a) { a = 1m; } }");
        AssertConversion(result, "ObjectHolder<Decimal> a", "a.value = Decimal.parse");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefNullableInt_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(ref int? a) { a = 1; } }");
        AssertConversion(result, "ObjectHolder<Integer> a", "a.value = 1");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutNullableDouble_ConvertsToObjectHolder()
    {
        var result = Convert("class C { public void M(out double? a) { a = 1.5; } }");
        AssertConversion(result, "ObjectHolder<Double> a", "a.value = 1.5");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefEnum_ConvertsToObjectHolder()
    {
        var result = Convert("enum Color { Red, Green, Blue } class C { public void M(ref Color a) { a = Color.Red; } }");
        AssertConversion(result, "ObjectHolder<Color> a", "a.value = Color.Red");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutString_ConvertsToObjectHolderWithWriteback()
    {
        var result = Convert("class C { public void M(out string a) { a = \"x\"; } }");
        AssertConversion(result, "ObjectHolder<String> a", "a.value = \"x\"");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InPrimitive_PassesByValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a; } }");
        AssertConversion(result, "public void m(int a)");
        Assert.False(result.GeneratedCode.Contains("IntHolder"),
            "in int parameter should not generate IntHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InReferenceType_PassesByValue()
    {
        var result = Convert("class C { public void M(in string a) { var x = a; } }");
        AssertConversion(result, "public void m(String a)");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder"),
            "in string parameter should not generate ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void InStruct_PassesByValue()
    {
        var result = Convert("struct Point { public int X; public int Y; } class C { public void M(in Point a) { var x = a.X; } }");
        AssertConversion(result, "public void m(Point a)");
        Assert.False(result.GeneratedCode.Contains("ObjectHolder"),
            "in struct parameter should not generate ObjectHolder");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MultiOutParameters_GeneratesMultipleHolders()
    {
        var result = Convert("class C { public void M(out int a, out double b) { a = 1; b = 1.5; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "a.value = 1", "b.value = 1.5");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void MixedRefOutIn_AllConvertedCorrectly()
    {
        var result = Convert("class C { public void M(ref int a, out double b, in string c) { a = 1; b = 1.5; var x = c; } }");
        AssertConversion(result, "IntHolder a", "DoubleHolder b", "String c");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void RefOutUsedInExpression_ValueAccess()
    {
        var result = Convert("class C { public int M(ref int a) { return a + 1; } }");
        AssertConversion(result, "IntHolder a", "return a.value + 1");
    }

    [Fact]
    [Trait("Category", "BasicTypes")]
    public void OutVarReadBack_AfterCall()
    {
        var result = Convert("class C { public bool Try(out int result) { result = 42; return true; } public void Call() { Try(out var r); var x = r; } }");
        AssertConversion(result, "IntHolder _rHolder1 = new IntHolder()", "tryValue(_rHolder1)", "int r = _rHolder1.value");
    }
}
