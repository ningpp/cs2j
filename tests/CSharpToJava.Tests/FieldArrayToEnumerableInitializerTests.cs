using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class FieldArrayToEnumerableInitializerTests
{
    [Fact]
    public void EnumerableFieldInitializedWithArray_IsWrapped()
    {
        const string code = """
            using System.Collections.Generic;

            public class C {
                IEnumerable<int> p = new[] { 1, 2 };
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("collect(Collectors.toList())", result.GeneratedCode);
    }
}
