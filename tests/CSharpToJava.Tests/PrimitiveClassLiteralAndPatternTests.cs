using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# byte maps to Java int in this converter's type system (sbyte maps to
/// byte), so typeof(byte) must emit int.class — byte.class (Class&lt;Byte&gt;)
/// would mismatch CSharpList&lt;Integer&gt; etc. Primitive `is` patterns must
/// also test the boxed wrapper type (instanceof Double), since Java's primitive
/// pattern matching is a preview feature.
/// </summary>
public class PrimitiveClassLiteralAndPatternTests
{
    [Fact]
    public void GenericTypeArgumentByte_EmitsUByteClassLiteral()
    {
        var result = Convert("""
class C {
    static Object Run<T>(List<T> list) => typeof(T);
    void Use() {
        Run<byte>(new List<byte>());
        Run<sbyte>(new List<sbyte>());
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        // C# byte maps to io.github.ningpp.compat.UByte (sbyte maps to byte), so
        // generic tokens follow the mapped Java type even though typeof(byte) keeps
        // a distinct CSharpByte marker at runtime.
        Assert.Contains("run(io.github.ningpp.compat.UByte.class, new CSharpList<io.github.ningpp.compat.UByte>())", code, StringComparison.Ordinal);
        Assert.Contains("run(byte.class, new CSharpList<Byte>())", code, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveIsPatterns_UseBoxedWrapperTypes()
    {
        var result = Convert("""
class C {
    String M(Object o) => o is double d ? "d" : o is float f ? "f" : "x";
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("instanceof Double", code, StringComparison.Ordinal);
        Assert.Contains("instanceof Float", code, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof double", code, StringComparison.Ordinal);
        Assert.DoesNotContain("instanceof float", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "PrimitiveClassLiteralAndPatternTests.cs",
            Options = new ConversionOptions(),
        });
    }
}
