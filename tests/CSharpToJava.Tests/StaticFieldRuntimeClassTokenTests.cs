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
