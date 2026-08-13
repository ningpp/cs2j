using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DeserializeGenericClassLiteralTests
{
    [Fact]
    public void DeserializeGenericType_EmitsRawClassLiteral()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class JsonSerializer
{
    public static T Deserialize<T>(string json)
    {
        return default;
    }
}

public class Sample
{
    public static List<int> M(string json)
    {
        return JsonSerializer.Deserialize<List<int>>(json);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("deserialize(CSharpList.class, json)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpList<Integer>.class", code, StringComparison.Ordinal);
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
