using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# nullable types, null-conditional (?.) and null-coalescing (??) operators
/// to Java equivalents (Integer/String, != null ? ... : ..., etc.).
/// </summary>
public class NullableTests : ConversionTestBase
{
    [Fact]
    public void NullableInt_ConvertsToInteger()
    {
        var result = Convert("class C { public int? X; public void M() { int? y = null; } }");
        AssertConversion(result, "public Integer X;", "Integer y = null;");
    }

    [Fact]
    public void NullableString_ConvertsToNullableString()
    {
        var result = Convert("class C { public string? S; }");
        AssertConversion(result, "public String S;");
    }

    [Fact]
    public void NullConditionalIndex_ConvertsWithTodoMarker()
    {
        var result = Convert("class D { public int Get() { int[] a = null; return a?[0] ?? 0; } }");
        AssertConversion(result, "TODO: ElementBindingExpression");
        AssertJavaDoesNotContain(result, "?[0]", "C# null-conditional indexer must be rewritten");
    }

    [Fact]
    public void NullCoalescing_ConvertsToTernary()
    {
        var result = Convert("class C { public int M(int? a) => a ?? 0; }");
        AssertConversion(result, "public int m(Integer a) {", "return a != null ? a : 0;");
        AssertJavaDoesNotContain(result, "??", "C# '??' must be rewritten");
    }

    [Fact]
    public void NullCoalescingString_ConvertsToTernary()
    {
        var result = Convert("class C { public string M(string a) => a ?? \"\"; }");
        AssertConversion(result, "return a != null ? a : \"\";");
    }

    [Fact]
    public void NullForgiving_StripsExclamation()
    {
        var result = Convert("class C { public string S = \"\"; public int L() => S!.Length; }");
        AssertConversion(result, "public int l() { return S.length(); }");
        AssertJavaDoesNotContain(result, "!", "C# null-forgiving '!' must be stripped");
    }

    [Fact]
    public void NullCoalescingChain_ConvertsToNestedTernary()
    {
        var result = Convert("class C { public string M(string a, string b) => a ?? b ?? \"\"; }");
        AssertConversion(result, "return a != null ? a : b != null ? b : \"\";");
    }

    [Fact]
    public void NullableNoCSharpResidue()
    {
        var result = Convert("class C { public int? X; public int M(int? a) => a ?? 0; }");
        AssertNoCSharpResidue(result);
    }
}
