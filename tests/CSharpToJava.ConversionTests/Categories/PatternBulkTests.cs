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
