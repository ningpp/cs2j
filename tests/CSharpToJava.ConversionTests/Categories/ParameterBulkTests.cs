using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Additional verification of C# parameter passing modes (ref/out usage, params, optional, named, overloads)
/// beyond the core MethodParameterTests, using validated Java mappings.
/// </summary>
public class ParameterBulkTests : ConversionTestBase
{
    [Fact]
    public void RefIntUsedInExpression_ConvertsToValueAccess()
    {
        var result = Convert("class C { public void M(ref int a) { a = a + 1; } }");
        AssertConversion(result, "public void m(IntHolder a) {", "a.value = a.value + 1;");
    }

    [Fact]
    public void RefLongUsedInExpression_ConvertsToValueAccess()
    {
        var result = Convert("class C { public void M(ref long a) { a = a * 2; } }");
        AssertConversion(result, "public void m(LongHolder a) {", "a.value = a.value * 2;");
    }

    [Fact]
    public void OutIntUsedInExpression_ConvertsToValueAccess()
    {
        var result = Convert("class C { public bool Try(out int v) { v = 5; return v > 0; } }");
        AssertConversion(result, "public boolean tryValue(IntHolder v) {", "v.value = 5;", "return v.value > 0;");
    }

    [Fact]
    public void OutDoubleUsedInExpression_ConvertsToValueAccess()
    {
        var result = Convert("class C { public bool Try(out double v) { v = 1.5; return v > 0; } }");
        AssertConversion(result, "public boolean tryValue(DoubleHolder v) {", "v.value = 1.5;", "return v.value > 0;");
    }

    [Fact]
    public void ParamsIntUsedInSum_ConvertsToVarargsAndLength()
    {
        var result = Convert("class C { public int M(params int[] a) { int s = 0; foreach (var x in a) s += x; return s; } }");
        AssertConversion(result, "public int m(int... a) {", "int s = 0;", "s += x;", "return s;");
    }

    [Fact]
    public void ParamsString_ConvertsToVarargs()
    {
        var result = Convert("class C { public void M(params string[] a) { var n = a.Length; } }");
        AssertConversion(result, "public void m(String... a) {", "var n = a.length;");
    }

    [Fact]
    public void OptionalIntUsed_ConvertsToOverload()
    {
        var result = Convert("class C { public int M(int a = 5) { return a * 2; } }");
        AssertConversion(result, "public int m(int a) {", "public int m() {", "m(5);");
    }

    [Fact]
    public void OptionalDouble_ConvertsToOverload()
    {
        var result = Convert("class C { public void M(double a = 1.0) { } }");
        AssertConversion(result, "public void m(double a) {", "public void m() {", "m(1.0);");
    }

    [Fact]
    public void NamedArgsThreeParams_Reorder()
    {
        var result = Convert("class C { public void M(int a, int b, int c) { } public void Call() { M(c: 3, a: 1, b: 2); } }");
        AssertConversion(result, "public void call() {", "m(1, 2, 3);");
    }

    [Fact]
    public void OverloadByThreeTypes_ConvertsToDistinctMethods()
    {
        var result = Convert("class C { public void M(int a) { } public void M(string a) { } public void M(double a) { } }");
        AssertConversion(result, "public void m(int a) {", "public void m(String a) {", "public void m(double a) {");
    }

    [Fact]
    public void InParameterUsed_ConvertsToValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a + 1; } }");
        AssertConversion(result, "public void m(int a) {", "var x = a + 1;");
    }

    [Fact]
    public void RefBoolAssignment_ConvertsToBoolHolder()
    {
        var result = Convert("class C { public void M(ref bool a) { a = true; } }");
        AssertConversion(result, "public void m(BoolHolder a) {", "a.value = true;");
    }

    [Fact]
    public void RefCharAssignment_ConvertsToCharHolder()
    {
        var result = Convert("class C { public void M(ref char a) { a = 'z'; } }");
        AssertConversion(result, "public void m(CharHolder a) {", "a.value = 'z';");
    }
}
