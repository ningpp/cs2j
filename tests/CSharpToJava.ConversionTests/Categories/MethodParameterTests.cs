using System.Collections.Generic;
using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# method parameter features (params, ref, out, optional, named, overloads, in).
/// </summary>
public class MethodParameterTests : ConversionTestBase
{
    // (csharpType, holder type, literal assigned to the holder's .value field)
    public static IEnumerable<object[]> RefHolderCases => new List<object[]>
    {
        new object[] { "int", "IntHolder", "1" },
        new object[] { "long", "LongHolder", "1" },
        new object[] { "double", "DoubleHolder", "1.5" },
        new object[] { "float", "FloatHolder", "1" },
        new object[] { "short", "ShortHolder", "1" },
        new object[] { "char", "CharHolder", "'x'" },
        new object[] { "bool", "BoolHolder", "true" },
        new object[] { "byte", "IntHolder", "1" },
        new object[] { "string", "ObjectHolder<String>", "\"x\"" },
    };

    [Theory]
    [MemberData(nameof(RefHolderCases))]
    public void RefParameter_ConvertsToHolder(string csharpType, string holder, string value)
    {
        var result = Convert($"class C {{ public void M(ref {csharpType} a) {{ a = {value}; }} }}");
        AssertConversion(result, $"public void m({holder} a) {{", $"a.value = {value};");
    }

    [Theory]
    [MemberData(nameof(RefHolderCases))]
    public void OutParameter_ConvertsToHolder(string csharpType, string holder, string value)
    {
        var result = Convert($"class C {{ public void M(out {csharpType} a) {{ a = {value}; }} }}");
        AssertConversion(result, $"public void m({holder} a) {{", $"a.value = {value};");
    }

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
    public void MultiRefParameters_ConvertsToMultipleHolders()
    {
        var result = Convert("class C { public void M(ref int a, ref int b) { a = 1; b = 2; } }");
        AssertConversion(result, "public void m(IntHolder a, IntHolder b) {", "a.value = 1;", "b.value = 2;");
    }

    [Fact]
    public void RefString_AssignsThroughObjectHolder()
    {
        var result = Convert("class C { public void M(ref string a) { a = \"x\"; } }");
        AssertConversion(result, "public void m(ObjectHolder<String> a) {", "a.value = \"x\";");
    }

    [Fact]
    public void OutBool_TryMethodRenamedAndUsesBoolHolder()
    {
        var result = Convert("class C { public bool Try(out bool v) { v = true; return true; } }");
        AssertConversion(result, "public boolean tryValue(BoolHolder v) {", "v.value = true;", "return true;");
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
    public void NamedArgs_ReorderPositionally()
    {
        var result = Convert("class C { public void M(int a, int b) { } public void Call() { M(b: 1, a: 2); } }");
        AssertConversion(result, "public void call() {", "m(2, 1);");
    }

    [Fact]
    public void NamedArgsMulti_ReorderPositionally()
    {
        var result = Convert("class C { public void M(int a, int b, int c) { } public void Call() { M(c: 3, a: 1, b: 2); } }");
        AssertConversion(result, "public void call() {", "m(1, 2, 3);");
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
