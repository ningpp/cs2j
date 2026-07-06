using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Additional verification of C# pattern matching constructs mapping to Java equivalents.
/// </summary>
public class PatternBulkTests : ConversionTestBase
{
    [Fact]
    public void IsDeclarationUsedInArithmetic_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is int i) { var x = i + 1; } } }");
        AssertConversion(result, "if (o instanceof Integer i) {", "var x = i + 1;");
    }

    [Fact]
    public void IsStringUsedInLength_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is string s) { var n = s.Length; } } }");
        AssertConversion(result, "if (o instanceof String s) {", "var n = s.length();");
    }

    [Fact]
    public void IsConstantMultiple_ConvertsToEquality()
    {
        var result = Convert("class C { public void M(object o) { if (o is 1 or 2 or 3) { } } }");
        AssertConversion(result, "if (((o == 1 || o == 2) || o == 3)) {");
    }

    [Fact]
    public void RelationalLessThan_ConvertsToComparison()
    {
        var result = Convert("class C { public void M(int x) { if (x is < 5) { } } }");
        AssertConversion(result, "if (x < 5) {");
    }

    [Fact]
    public void RelationalGreaterEqual_ConvertsToComparison()
    {
        var result = Convert("class C { public void M(int x) { if (x is >= 10) { } } }");
        AssertConversion(result, "if (x >= 10) {");
    }

    [Fact]
    public void LogicalAndPattern_ConvertsToAnd()
    {
        var result = Convert("class C { public void M(int x) { if (x is > 0 and < 100) { } } }");
        AssertConversion(result, "if ((x > 0 && x < 100)) {");
    }

    [Fact]
    public void SwitchWhenWithGuard_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object x) { switch(x) { case int i when i > 0: break; } } }");
        AssertConversion(result, "if ((x instanceof int i) && i > 0) {");
    }

    [Fact]
    public void SwitchExpressionTwoArms_ConvertsToTernary()
    {
        var result = Convert("class C { public int M(int x) => x switch { 1 => 10, 2 => 20, _ => 0 }; }");
        AssertConversion(result, "return (Objects.equals(x, 1) ? 10 : (Objects.equals(x, 2) ? 20 : 0));");
    }

    [Fact]
    public void SwitchExpressionSingleArm_ConvertsToTernary()
    {
        var result = Convert("class C { public string M(int x) => x switch { 0 => \"zero\", _ => \"other\" }; }");
        AssertConversion(result, "return (Objects.equals(x, 0) ? \"zero\" : \"other\");");
    }

    [Fact]
    public void NegatedNullPattern_ConvertsToNotEquals()
    {
        var result = Convert("class C { public void M(object o) { if (o is not null) { var x = 1; } } }");
        AssertConversion(result, "if (!(o == null)) {", "var x = 1;");
    }

    [Fact]
    public void NotTypePattern_ConvertsToNegatedInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is not string) { var x = 1; } } }");
        AssertConversion(result, "if (!(o instanceof String)) {", "var x = 1;");
    }

    [Fact]
    public void IsTypeWithAndPattern_ConvertsToInstanceofAnd()
    {
        var result = Convert("class C { public void M(object o) { if (o is int i && i > 5) { var x = i; } } }");
        AssertConversion(result, "if (o instanceof Integer i && i > 5) {", "var x = i;");
    }

    [Fact]
    public void PatternMatchingNoCSharpResidue()
    {
        var result = Convert("class C { public void M(object o) { if (o is 1 or 2 or 3) { var x = 1; } } }");
        AssertNoCSharpResidue(result);
    }
}
