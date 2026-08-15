using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// The unsigned rewriter lowers `(uint)a + (uint)b >= int.MaxValue` to
/// `UnsignedMath.CompareInt64(ToInt64UInt32(AddUInt32(...)), int.MaxValue) >= 0`.
/// The converter must NOT wrap the already-signed CompareInt64 result in
/// Integer.compareUnsigned(..., 0); doing so turns -1 into a huge positive
/// value and breaks overflow checks (e.g. RegexNode canCombineCounts never
/// merges adjacent loops).
/// </summary>
public class UnsignedCompareHelperResultTests
{
    [Fact]
    public void CompareHelperResult_NotWrappedInCompareUnsigned()
    {
        var result = Convert("""
namespace Cs2jUnsigned
{
    public static class UnsignedMath
    {
        public static int AddUInt32(int a, int b) => unchecked(a + b);
        public static long ToInt64UInt32(int v) => v & 0xFFFFFFFFL;
        public static int CompareInt64(long a, long b) => a < b ? -1 : a > b ? 1 : 0;
    }
}

class C
{
    bool M(int a, int b)
    {
        return Cs2jUnsigned.UnsignedMath.CompareInt64(
            Cs2jUnsigned.UnsignedMath.ToInt64UInt32(
                Cs2jUnsigned.UnsignedMath.AddUInt32((int)(uint)a, (int)(uint)b)),
            int.MaxValue) >= 0;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("compareInt64(", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compareUnsigned(UnsignedMath.compareInt64", code, StringComparison.Ordinal);
        Assert.Contains("compareInt64(", code, StringComparison.Ordinal);
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
