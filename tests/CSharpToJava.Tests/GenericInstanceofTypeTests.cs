using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class GenericInstanceofTypeTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void AsOperatorOnGenericType_DoesNotEmitParameterizedInstanceof()
    {
        const string code = """
            class Box<T>
            {
                bool EqualsBox(object obj)
                {
                    Box<T> other = obj as Box<T>;
                    return other != null;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("instanceof Box", result.GeneratedCode);
        Assert.DoesNotContain("instanceof Box<T>", result.GeneratedCode);
    }
}
