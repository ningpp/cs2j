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
    public void IsTypePattern_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is string) { } } }");
        AssertConversion(result, "if (o instanceof String) {");
    }





}
