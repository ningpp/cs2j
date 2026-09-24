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
    public void ParamsUsage_ConvertsToArrayLength()
    {
        var result = Convert("class C { public int M(params int[] a) { return a.Length; } }");
        AssertConversion(result, "public int m(int... a) {", "a.length");
    }

    [Fact]
    public void OptionalInt_ConvertsToOverloadWithDefault()
    {
        var result = Convert("class C { public void M(int a = 5) { } }");
        AssertConversion(result, "public void m(int a) {", "public void m() {", "m(5);");
    }

    [Fact]
    public void OptionalString_ConvertsToOverloadWithDefault()
    {
        var result = Convert("class C { public void M(string a = \"x\") { } }");
        AssertConversion(result, "public void m(String a) {", "public void m() {", "m(\"x\");");
    }

    [Fact]
    public void OptionalMulti_ConvertsToMultipleOverloads()
    {
        var result = Convert("class C { public void M(int a = 1, int b = 2) { } }");
        AssertConversion(result,
            "public void m(int a, int b) {",
            "public void m() {", "m(1, 2);",
            "public void m(int a) {", "m(a, 2);");
    }

    [Fact]
    public void DefaultLiteral_ConvertsToOverload()
    {
        var result = Convert("class C { public void M(int a = default) { } }");
        AssertConversion(result, "public void m(int a) {", "public void m() {", "m(0);");
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
