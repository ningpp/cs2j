using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StaticFieldRuntimeClassTokenTests
{
    [Fact]
    public void StaticFieldWithTypeofT_DemotesToInstanceField_InitializedInCtor()
    {
        var result = Convert(@"
using System;

public class EnumConverter<T> where T : Enum
{
    private static readonly Type s_enumTypeCode = Type.GetTypeCode(typeof(T));

    public int GetCode()
    {
        return s_enumTypeCode.GetHashCode();
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("private final Class s_enumTypeCode", code, StringComparison.Ordinal);
        Assert.DoesNotContain("static final", code, StringComparison.Ordinal);
        Assert.DoesNotContain("static Class s_enumTypeCode", code, StringComparison.Ordinal);
        Assert.Contains("this.s_enumTypeCode = ", code, StringComparison.Ordinal);
    }

    [Fact]
    public void StaticFieldChainAndStaticHelper_AreDemotedTransitively()
    {
        var result = Convert(@"
using System;

public class EnumConverter<T> where T : Enum
{
    private static readonly Type s_enumTypeCode = Type.GetTypeCode(typeof(T));
    private static readonly string s_negativeSign = ((int)s_enumTypeCode.GetHashCode() % 2) == 0 ? null : ""-"";

    public static long ConvertToUInt64(object value)
    {
        return s_enumTypeCode.GetHashCode() + (s_negativeSign == null ? 0 : 1);
    }

    public int Use()
    {
        return (int)ConvertToUInt64(null);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("static", code, StringComparison.Ordinal);
        Assert.Contains("this.s_enumTypeCode = ", code, StringComparison.Ordinal);
        Assert.Contains("this.s_negativeSign = ", code, StringComparison.Ordinal);
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
