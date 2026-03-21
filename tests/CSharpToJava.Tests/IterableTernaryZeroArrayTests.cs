using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class IterableTernaryZeroArrayTests
{
    [Fact]
    public void IterableReturn_TernaryWithZeroArray_UsesEmptyList()
    {
        const string code = """
            using System.Collections.Generic;

            public class C<T> {
                IEnumerable<T> M(bool ok, IEnumerable<T> items) {
                    return ok ? items : new T[0];
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Collections.emptyList()", result.GeneratedCode);
    }
}
