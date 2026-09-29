using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# pattern matching (is/var/constant/relational/logical/property patterns,
/// switch with when, switch expressions, or/not patterns) to Java equivalent constructs.
/// </summary>
public class PatternMatchingTests : ConversionTestBase
{
    [Fact]
    public void IsDeclarationPattern_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is int i) { var x = i; } } }");
        AssertConversion(result, "if (o instanceof Integer i) {", "var x = i;");
    }

    [Fact]
    public void IsDeclarationUsed_ConvertsToInstanceofWithBody()
    {
        var result = Convert("class C { public void M(object o) { if (o is string s) { var n = s.Length; } } }");
        AssertConversion(result, "if (o instanceof String s) {", "var n = s.length();");
    }

    [Fact]
    public void IsConstantPattern_ConvertsToEquality()
    {
        var result = Convert("class C { public void M(object o) { if (o is 5) { } } }");
        AssertConversion(result, "if (o == 5) {");
    }

    [Fact]
    public void IsTypePattern_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is string) { } } }");
        AssertConversion(result, "if (o instanceof String) {");
    }

    [Fact]
    public void RelationalPattern_ConvertsToComparison()
    {
        var result = Convert("class C { public void M(int x) { if (x is > 0) { } } }");
        AssertConversion(result, "if (x > 0) {");
    }

    [Fact]
    public void LogicalPattern_ConvertsToAnd()
    {
        var result = Convert("class C { public void M(int x) { if (x is >= 0 and <= 10) { } } }");
        AssertConversion(result, "if ((x >= 0 && x <= 10)) {");
    }

    [Fact(Skip = "C37: expression-side recursive/positional/list pattern reducer")]
    public void PropertyPattern_ConvertsToMemberComparison()
    {
        // C37 lowers `p is { X: 1 }` to a null guard plus member comparison on the
        // Java side. The previous expectation asserted a `/* TODO: recursive
        // pattern */` marker that no longer exists anywhere in src; the case stays
        // skipped until C37 lands (see the C03 design section 7.2).
        var result = Convert("class C { public void M(P p) { if (p is { X: 1 }) { } } } public class P { public int X; }");
        AssertConversion(result, "p != null", "p.X == 1");
    }

    [Fact]
    public void NegatedPattern_ConvertsToNotEqualsNull()
    {
        var result = Convert("class C { public void M(object o) { if (o is not null) { } } }");
        AssertConversion(result, "if (!(o == null)) {");
    }

    [Fact]
    public void OrPattern_ConvertsToOr()
    {
        var result = Convert("class C { public void M(object o) { if (o is 1 or 2) { } } }");
        AssertConversion(result, "if ((o == 1 || o == 2)) {");
    }

    [Fact]
    public void NotTypePattern_ConvertsToNegatedInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is not string) { } } }");
        AssertConversion(result, "if (!(o instanceof String)) {");
    }

    [Fact]
    public void PatternMatchingNoCSharpResidue()
    {
        var result = Convert("class C { public void M(object o) { if (o is int i && i > 0) { var x = i; } } }");
        AssertNoCSharpResidue(result);
    }
}
