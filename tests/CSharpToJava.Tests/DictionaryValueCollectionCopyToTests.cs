using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DictionaryValueCollectionCopyToTests
{
    [Fact]
    public void DictionaryValuesCopyTo_MapsToCSharpArrayHelper()
    {
        var result = Convert(@"
using System.Collections.Generic;

public class Sample
{
    public void M(Dictionary<string, int> dict, int[] arr)
    {
        dict.Values.CopyTo(arr, 0);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CSharpArray.copyTo(", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.CSharpArray", code, StringComparison.Ordinal);
        Assert.DoesNotContain("values().copyTo(", code, StringComparison.Ordinal);
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
