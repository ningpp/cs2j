using Xunit;

namespace CSharpToJava.ConversionTests.Categories;

/// <summary>
/// Verifies correct conversion of C# extension methods (static classes with 'this' parameters) to Java
/// static utility classes with the receiver as a normal first parameter.
/// </summary>
public class ExtensionMethodTests : ConversionTestBase
{




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


}
