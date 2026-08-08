using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class UIntComparisonTests
{
    [Fact]
    public void UInt_Cast_Comparison_UsesCompareUnsigned()
    {
        var result = Convert("""
class Program
{
    static bool IsHexDigit(char c)
    {
        return (uint)(c - '0') <= '9' - '0'
            || (uint)(c - 'A') <= 'F' - 'A'
            || (uint)(c - 'a') <= 'f' - 'a';
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // Should use Integer.compareUnsigned instead of direct <= comparison
        Assert.Contains("Integer.compareUnsigned", result.GeneratedCode, StringComparison.Ordinal);
        // Should NOT contain the broken pattern: (int)((... & 0xFFFFFFFFL) <= ...
        Assert.DoesNotContain("& 0xFFFFFFFFL) <=", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("& 0xFFFFFFFFL) >=", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt_Variable_Comparison_UsesCompareUnsigned()
    {
        var result = Convert("""
class Program
{
    static bool Check(uint a, uint b)
    {
        return a <= b;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains("Integer.compareUnsigned", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt_LessThan_UsesCompareUnsigned()
    {
        var result = Convert("""
class Program
{
    static bool Check(uint a, uint b)
    {
        return a < b;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains("Integer.compareUnsigned", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("< 0", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UInt_CompoundOperand_Comparison_DoesNotMangleOperand()
    {
        // RegexNode.CanCombineCounts: (uint)nodeMin + (uint)nextMin >= int.MaxValue.
        // The uint-typed operand is a compound expression; the mask-stripping must not
        // treat the whole addition as a single (uint) cast.
        var result = Convert("""
class Program
{
    static bool CanCombine(int nodeMin, int nextMin)
    {
        return (uint)nodeMin + (uint)nextMin >= int.MaxValue;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains("Integer.compareUnsigned", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(", Integer.MAX_VALUE) >= 0", result.GeneratedCode, StringComparison.Ordinal);
        // The two masked operands of the addition must both survive intact.
        Assert.DoesNotContain("(nextMin,", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
