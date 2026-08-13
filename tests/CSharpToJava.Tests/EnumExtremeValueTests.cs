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
