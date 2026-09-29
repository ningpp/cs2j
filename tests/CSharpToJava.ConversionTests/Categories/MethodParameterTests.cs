using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# method parameter features (params, ref, out, optional, named, overloads, in).
/// </summary>
public class MethodParameterTests : ConversionTestBase
{
    [Fact]
    public void ParamsIntArray_ConvertsToVarargs()
    {
        var result = Convert("class C { public void M(params int[] a) { } }");
        AssertConversion(result, "public void m(int... a) {");
    }

    [Fact]
    public void ParamsStringArray_ConvertsToVarargs()
    {
        var result = Convert("class C { public void M(params string[] a) { } }");
        AssertConversion(result, "public void m(String... a) {");
    }











    [Fact]
    public void OverloadByType_ConvertsToDistinctMethods()
    {
        var result = Convert("class C { public void M(int a) { } public void M(string a) { } }");
        AssertConversion(result, "public void m(int a) {", "public void m(String a) {");
    }

    [Fact]
    public void InParameter_DroppedToValue()
    {
        var result = Convert("class C { public void M(in int a) { var x = a; } }");
        AssertConversion(result, "public void m(int a) {", "var x = a;");
    }
}
