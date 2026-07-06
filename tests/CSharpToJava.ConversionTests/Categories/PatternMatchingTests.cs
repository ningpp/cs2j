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
    public void SwitchWithWhen_ConvertsToInstanceofGuard()
    {
        var result = Convert("class C { public void M(int x) { switch(x) { case int i when i > 0: break; } } }");
        AssertConversion(result, "if ((x instanceof int i) && i > 0) {");
    }

    [Fact]
    public void SwitchWithWhenMulti_ConvertsToInstanceofGuard()
    {
        var result = Convert("class C { public void M(int x) { switch(x) { case int i when i > 0 && i < 5: break; } } }");
        AssertConversion(result, "if ((x instanceof int i) && i > 0 && i < 5) {");
    }

    [Fact]
    public void SwitchExpression_ConvertsToTernary()
    {
        var result = Convert("class C { public int M(int x) => x switch { 1 => 10, _ => 0 }; }");
        AssertConversion(result, "import java.util.Objects;", "return (Objects.equals(x, 1) ? 10 : 0);");
    }

    [Fact]
    public void SwitchExpressionMulti_ConvertsToNestedTernary()
    {
        var result = Convert("class C { public int M(int x) => x switch { 1 => 10, 2 => 20, _ => 0 }; }");
        AssertConversion(result, "return (Objects.equals(x, 1) ? 10 : (Objects.equals(x, 2) ? 20 : 0));");
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

    [Fact]
    public void PropertyPattern_ConvertsWithTodoMarker()
    {
        var result = Convert("class C { public void M(P p) { if (p is { X: 1 }) { } } } public class P { public int X; }");
        AssertConversion(result, "/* TODO: recursive pattern */ p");
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
