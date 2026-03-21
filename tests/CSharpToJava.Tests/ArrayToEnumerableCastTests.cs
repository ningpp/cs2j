using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class ArrayToEnumerableCastTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void CastArrayToIEnumerable_UsesArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                public C(IEnumerable<string> points) { }
                public C(params string[] points) : this((IEnumerable<string>)points) { }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("this(Arrays.asList(points))", result.GeneratedCode);
        Assert.DoesNotContain("(Iterable<String>)(points)", result.GeneratedCode);
    }
}
