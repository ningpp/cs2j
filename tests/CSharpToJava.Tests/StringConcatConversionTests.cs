using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringConcatConversionTests
{
    [Fact]
    public void StringConcat_ParamsArray_UsesStringHelperCompatCall()
    {
        const string code = """
            using System;

            public class C
            {
                public string JoinAll(params string[] values)
                {
                    return String.Concat(values);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("StringHelper.concat(values)", result.GeneratedCode);
        Assert.DoesNotContain("String.concat(values)", result.GeneratedCode);
    }
}