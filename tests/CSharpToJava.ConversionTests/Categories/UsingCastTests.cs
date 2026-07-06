using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# using statements (try-with-resources) and cast expressions
/// (explicit casts, as/is patterns) to Java.
/// </summary>
public class UsingCastTests : ConversionTestBase
{
    [Fact]
    public void UsingVar_ConvertsToTryWithResources()
    {
        var result = Convert("class C { public void M() { using var s = new System.IO.MemoryStream(); } }");
        AssertConversion(result,
            "import io.github.ningpp.compat.MemoryStream;",
            "try (MemoryStream s = new MemoryStream()) {");
    }

    [Fact]
    public void UsingBlock_ConvertsToTryWithResources()
    {
        var result = Convert("class C { public void M() { using (var s = new System.IO.MemoryStream()) { var x = 1; } } }");
        AssertConversion(result, "try (MemoryStream s = new MemoryStream()) {", "var x = 1;");
    }

    [Fact]
    public void UsingTwoResources_ConvertsToSingleTry()
    {
        var result = Convert("class C { public void M() { using var a = new System.IO.MemoryStream(); using var b = new System.IO.MemoryStream(); } }");
        AssertConversion(result, "try (MemoryStream a = new MemoryStream(); MemoryStream b = new MemoryStream()) {");
    }

    [Fact]
    public void NestedUsing_ConvertsToNestedTry()
    {
        var result = Convert("class C { public void M() { using (var s = new System.IO.MemoryStream()) { using (var t = new System.IO.MemoryStream()) { } } } }");
        AssertConversion(result,
            "try (MemoryStream s = new MemoryStream()) {",
            "try (MemoryStream t = new MemoryStream()) {");
    }

    [Fact]
    public void IDisposableClass_ConvertsToAutoCloseable()
    {
        var result = Convert("class C : System.IDisposable { public void Dispose() { } } class D { public void M() { using var c = new C(); } }");
        AssertConversion(result,
            "public class C implements AutoCloseable {",
            "public void close() {",
            "try (C c = new C()) {");
        AssertJavaDoesNotContain(result, "Dispose", "C# 'Dispose' must become Java 'close'");
    }

    [Fact]
    public void ExplicitCast_ConvertsToParenthesizedCast()
    {
        var result = Convert("class C { public int M(object o) => (int)o; }");
        AssertConversion(result, "return (int)(o);");
    }

    [Fact]
    public void AsCast_ConvertsToInstanceofTernary()
    {
        var result = Convert("class C { public string M(object o) => o as string; }");
        AssertConversion(result, "public String m(Object o) { return (o instanceof String ? (String)(o) : null); }");
    }

    [Fact]
    public void IsPatternWithCast_ConvertsToInstanceof()
    {
        var result = Convert("class C { public void M(object o) { if (o is string s) { } string x = o as string; } }");
        AssertConversion(result,
            "if (o instanceof String s) {",
            "String x = (o instanceof String ? (String)(o) : null);");
    }

    [Fact]
    public void CastInExpression_ConvertsToParenthesizedCast()
    {
        var result = Convert("class C { public double M(int a) => (double)a / 2; }");
        AssertConversion(result, "return (double)(a) / 2;");
    }

    [Fact]
    public void AsNullCoalesce_ConvertsToCoalesceVariable()
    {
        var result = Convert("class C { public string M(object o) => (o as string) ?? \"\"; }");
        AssertConversion(result,
            "var _coalesce1 = ((o instanceof String ? (String)(o) : null));",
            "return _coalesce1 != null ? _coalesce1 : \"\";");
    }
}
