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
    public void NullableList_ConvertsToCSharpList()
    {
        var result = Convert("class C { public System.Collections.Generic.List<int>? L; }");
        AssertConversion(result, "public CSharpList<Integer> L;");
    }

    [Fact]
    public void NullConditionalProperty_ConvertsToTernary()
    {
        var result = Convert("class C { public int X; } class D { public int Get() { C c = null; return c?.X ?? 0; } }");
        AssertConversion(result, "return (c != null ? c.getX() : 0);");
        AssertJavaDoesNotContain(result, "?.X", "C# null-conditional member access must be rewritten");
    }

    [Fact]
    public void NullConditionalMethod_ConvertsToIfGuard()
    {
        var result = Convert("class C { public void M() { } } class D { public void Call() { C c = null; c?.M(); } }");
        AssertConversion(result, "if (c != null) { c.M(); }");
        AssertJavaDoesNotContain(result, "?.M()", "C# null-conditional call must be rewritten");
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
    public void NullCoalescingAssignment_ConvertsToNullCheck()
    {
        var result = Convert("class C { public void M(System.Collections.Generic.List<int> l) { l ??= new System.Collections.Generic.List<int>(); } }");
        AssertConversion(result, "if (l == null) l = new CSharpList<Integer>();");
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
