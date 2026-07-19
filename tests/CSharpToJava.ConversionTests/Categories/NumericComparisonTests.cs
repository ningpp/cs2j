using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# primitive CompareTo methods to Java static helpers.
/// </summary>
public class NumericComparisonTests : ConversionTestBase
{
    [Fact]
    public void IntCompareTo_ConvertsToIntegerCompare()
    {
        var result = Convert("class C { public int M(int a, int b) { return a.CompareTo(b); } }");
        AssertConversion(result, "return Integer.compare(a, b);");
    }

    [Fact]
    public void LongCompareTo_ConvertsToLongCompare()
    {
        var result = Convert("class C { public int M(long a, long b) { return a.CompareTo(b); } }");
        AssertConversion(result, "return Long.compare(a, b);");
    }
}
