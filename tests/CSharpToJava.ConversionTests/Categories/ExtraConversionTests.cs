using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Additional conversion-correctness tests covering a mix of constructs whose Java mappings have been
/// validated: literals, string/number operations, control flow, concurrency, reflection and using statements.
/// </summary>
public class ExtraConversionTests : ConversionTestBase
{
    [Fact] public void LocalIntLiterals_ConvertIdentically() => AssertConversion(Convert("class C { public void M() { int a = 5; int b = 10; } }"), "int a = 5;", "int b = 10;");
    [Fact] public void StringConcat_ConvertsToPlus() => AssertConversion(Convert("class C { public string M(string a, string b) { return a + b; } }"), "return a + b;");
    [Fact] public void StringLength_ConvertsToLength() => AssertConversion(Convert("class C { public int M(string s) { return s.Length; } }"), "return s.length();");
    [Fact] public void StringToUpper_ConvertsToUpperCase() => AssertConversion(Convert("class C { public string M(string s) { return s.ToUpper(); } }"), "return s.toUpperCase();");
    [Fact] public void MathAbs_ConvertsToAbs() => AssertConversion(Convert("class C { public int M(int a) { return System.Math.Abs(a); } }"), "return Math.abs(a);");
    [Fact] public void MathMax_ConvertsToMax() => AssertConversion(Convert("class C { public int M(int a, int b) { return System.Math.Max(a, b); } }"), "return Math.max(a, b);");
    [Fact] public void Ternary_ConvertsWithParens() => AssertConversion(Convert("class C { public int M(int a) { return a > 0 ? a : -a; } }"), "return (a > 0 ? a : -a);");
    [Fact] public void Foreach_ConvertsToEnhancedFor() => AssertConversion(Convert("class C { public void M() { foreach (var x in new int[] { 1, 2 }) { var y = x; } } }"), "for (int x : new int[] { 1, 2 }) {", "var y = x;");
    [Fact] public void Lock_ConvertsToSynchronized() => AssertConversion(Convert("class C { private readonly object l = new object(); public void M() { lock (l) { var x = 1; } } }"), "synchronized (l) {", "var x = 1;");
    [Fact] public void NameOf_ConvertsToLiteral() => AssertConversion(Convert("class C { public int X; public string N() { return nameof(X); } }"), "return \"X\";");
    [Fact] public void TypeOf_ConvertsToClassLiteral() => AssertConversion(Convert("class C { public System.Type T() { return typeof(int); } }"), "return int.class;");
    [Fact] public void SizeOf_ConvertsToConstant() => AssertConversion(Convert("class C { public int S() { return sizeof(int); } }"), "return 4;");
    [Fact] public void NullCoalesceString_ConvertsToTernary() => AssertConversion(Convert("class C { public string M(string a) { return a ?? \"\"; } }"), "return a != null ? a : \"\";");
    [Fact] public void IsPattern_ConvertsToInstanceof() => AssertConversion(Convert("class C { public void M(object o) { if (o is int i) { var x = i; } } }"), "if (o instanceof Integer i) {", "var x = i;");
    [Fact] public void UsingVar_ConvertsToTryWithResources() => AssertConversion(Convert("class C { public void M() { using var s = new System.IO.MemoryStream(); } }"), "try (MemoryStream s = new MemoryStream()) {");
    [Fact] public void NoCSharpResidue_Extra()
    {
        var result = Convert("class C { public string M(string s) { return s.ToUpper().Trim(); } }");
        AssertNoCSharpResidue(result);
    }
}
