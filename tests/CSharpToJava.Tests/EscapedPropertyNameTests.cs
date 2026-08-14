using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Property names written as Unicode escapes in the C# source (e.g. \u0467)
/// must be emitted from the semantic symbol's real characters, not the raw
/// escape text. Otherwise the getter declaration (get\u0467) and the getter
/// call site (getѦ) disagree and javac reports "cannot find symbol".
/// </summary>
public class EscapedPropertyNameTests
{
    [Fact]
    public void EscapedPropertyName_GetterDeclarationMatchesCallSite()
    {
        var result = Convert("""
class C {
    public int \u0467 { get; set; }
    int M() => \u0467;
    C Make() => new C { \u0467 = 2 };
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("public int getѦ()", code, StringComparison.Ordinal);
        Assert.Contains("return getѦ();", code, StringComparison.Ordinal);
        Assert.Contains("setѦ(2);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("get\\u0467", code, StringComparison.Ordinal);
        Assert.DoesNotContain("set\\u0467", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "EscapedPropertyNameTests.cs",
            Options = new ConversionOptions(),
        });
    }
}
