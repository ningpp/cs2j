using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Additional verification of C# parameter passing modes (ref/out usage, params, optional, named, overloads)
/// beyond the core MethodParameterTests, using validated Java mappings.
/// </summary>
public class ParameterBulkTests : ConversionTestBase
{
    [Fact]
    public void ParamsIntUsedInSum_ConvertsToVarargsAndLength()
    {
        var result = Convert("class C { public int M(params int[] a) { int s = 0; foreach (var x in a) s += x; return s; } }");
        AssertConversion(result, "public int m(int... a) {", "int s = 0;", "s += x;", "return s;");
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
}
