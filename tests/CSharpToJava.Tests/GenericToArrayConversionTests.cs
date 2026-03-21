using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericToArrayConversionTests
{
    [Fact]
    public void GenericEnumerableToArray_UsesObjectArrayLambdaCast()
    {
        const string code = """
            using System.Linq;
            using System.Collections.Generic;

            public class C<T> {
                public T[] M(IEnumerable<T> items) {
                    return items.ToArray();
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("toArray(size -> (T[]) new Object[size])", result.GeneratedCode);
    }

    [Fact]
    public void ImplicitArrayCreation_WithGenericElement_UsesRawArrayType()
    {
        const string code = """
            using System.Collections.Generic;

            class C {
                List<KeyValuePair<int, string>> M() {
                    var arr = new[] { new KeyValuePair<int, string>(1, "a") };
                    return new List<KeyValuePair<int, string>>(arr);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new AbstractMap.SimpleEntry[]", result.GeneratedCode);
    }
}
