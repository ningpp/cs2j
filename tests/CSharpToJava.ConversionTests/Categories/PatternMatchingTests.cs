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





    [Fact]
    public void GenericDeclarationPattern_KeepsGenericVariableWithRawInstanceofGuard()
    {
        // JavaGenericPatternRewriter hoists `o is List<int> l` into a type-only
        // pattern plus a cast declaration: the guard must survive as a raw-type
        // instanceof (Java forbids generic instanceof), never as `true`.
        var result = Convert(
            "using System.Collections.Generic; class C { public int M(object o) { if (o is List<int> l) { return l.Count; } return 0; } }");
        AssertConversion(result,
            "if (o instanceof CSharpList) {",
            "CSharpList<Integer> l = (CSharpList<Integer>)(o);");
        AssertJavaDoesNotContain(result, "unsupported pattern",
            "Generic declaration pattern must not fall back to `true`.");
    }





}
