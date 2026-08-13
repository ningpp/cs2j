using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnumExtremeValueTests
{
    [Fact]
    public void UIntEnumMaxValue_EmitWrappedSignedInt()
    {
        var result = Convert(@"
public enum SampleEnumUInt32 : uint
{
    MinZero = 0,
    One = 1,
    Max = uint.MaxValue
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Max(-1)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Max(4294967295)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ULongEnumMaxValue_EmitWrappedSignedLong()
    {
        var result = Convert(@"
public enum SampleEnumUInt64 : ulong
{
    MinZero = 0,
    One = 1,
    Max = ulong.MaxValue
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("Max(-1L)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("0xFFFFFFFFFFFFFFFF", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyCollectionInitializerOnField_EmitsPlainConstructor()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class Sample
{
    private List<int> _myIntList = new List<int> { };
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("_myIntList = new CSharpList<Integer>()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("var _obj", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LongEnumMinMaxValue_NoSpuriousLongSuffix()
    {
        var result = Convert(@"
public enum SampleEnumInt64 : long
{
    MinNegative = long.MinValue,
    Zero = 0,
    One = 1,
    Two = 2,
    Max = long.MaxValue
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MinNegative(Long.MIN_VALUE)", code, StringComparison.Ordinal);
        Assert.Contains("Max(Long.MAX_VALUE)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MIN_VALUEL", code, StringComparison.Ordinal);
        Assert.DoesNotContain("MAX_VALUEL", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LongEnumNegativeLiteral_KeepsLongSuffix()
    {
        var result = Convert(@"
public enum SampleEnumInt64 : long
{
    MinNegative = -9223372036854775808,
    Max = 9223372036854775807
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MinNegative(-9223372036854775808L)", code, StringComparison.Ordinal);
        Assert.Contains("Max(9223372036854775807L)", code, StringComparison.Ordinal);
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
