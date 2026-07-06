using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# extension methods (static classes with 'this' parameters) to Java
/// static utility classes with the receiver as a normal first parameter.
/// </summary>
public class ExtensionMethodTests : ConversionTestBase
{
    [Fact]
    public void ExtensionOnInt_ConvertsToStaticMethod()
    {
        var result = Convert("public static class E { public static int Double(this int x) => x * 2; }");
        AssertConversion(result,
            "public final class E {",
            "private E() {",
            "public static int doubleValue(int x) { return x * 2; }");
    }

    [Fact]
    public void ExtensionOnString_ConvertsToStaticMethod()
    {
        var result = Convert("public static class E { public static void Log(this string s) { } }");
        AssertConversion(result, "public final class E {", "public static void log(String s) {");
    }

    [Fact]
    public void ExtensionUsage_ConvertsToStaticCall()
    {
        var result = Convert("public static class E { public static int Square(this int x) => x * x; } class C { public int M() => 3.Square(); }");
        AssertConversion(result, "public static int square(int x) { return x * x; }", "public int m() { return E.square(3); }");
    }

    [Fact]
    public void ExtensionWithExtraParam_ConvertsToStaticMethod()
    {
        var result = Convert("public static class E { public static int Add(this int x, int y) => x + y; }");
        AssertConversion(result, "public static int add(int x, int y) { return x + y; }");
    }

    [Fact]
    public void GenericExtension_ConvertsToGenericStaticMethod()
    {
        var result = Convert("public static class E { public static T Id<T>(this T x) => x; }");
        AssertConversion(result, "public static <T> T id(T x) { return x; }");
    }

    [Fact]
    public void ExtensionClassIsFinalWithPrivateCtor()
    {
        var result = Convert("public static class E { public static void M(this int x) { } }");
        AssertConversion(result, "public final class E {", "private E() {");
        AssertJavaDoesNotContain(result, "static class", "C# static class must become Java final class");
    }
}
