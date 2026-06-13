using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# ushort variable ++/-- operations correctly wrap around
/// in Java, matching C# unchecked overflow semantics.
/// In C#, ushort wraps: --i when i=0 gives 65535.
/// In Java, int doesn't wrap: --i when i=0 gives -1.
/// The converter must add &amp; 0xFFFF masking to simulate wrap-around.
/// </summary>
public class UShortIncrementDecrementTests
{
    [Fact]
    public void UShort_PrefixDecrement_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M() {
        ushort i = 5;
        --i;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // --i for ushort should become i = (((int)(i - 1)) & 0xFFFF)
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("--i", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShort_PrefixIncrement_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M() {
        ushort i = 5;
        ++i;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // ++i for ushort should become i = (((int)(i + 1)) & 0xFFFF)
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("++i", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShort_ForLoopDecrement_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M(ushort start) {
        for (ushort i = 10; i != start; --i) {
            var x = i;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // --i in for loop for ushort should use & 0xFFFF masking
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UShort_PostfixDecrement_UsesBitwiseMask()
    {
        var result = Convert(@"
class Test {
    void M() {
        ushort i = 5;
        i--;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // i-- for ushort should use & 0xFFFF masking
        Assert.Contains("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("i--", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Int_Decrement_NotAffected()
    {
        var result = Convert(@"
class Test {
    void M() {
        int i = 5;
        --i;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // --i for int should remain as --i (no masking)
        Assert.Contains("--i", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFFFF", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
