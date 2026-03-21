using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class SystemArrayReflectionConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ArrayCreateInstanceAndSetValue_UseJavaReflectArrayHelpers()
    {
        const string code = """
            class C
            {
                void M()
                {
                    System.Array arr = System.Array.CreateInstance(typeof(string), 3);
                    arr.SetValue("x", 0);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("java.lang.reflect.Array.newInstance", result.GeneratedCode);
        Assert.Contains("java.lang.reflect.Array.set(arr, 0, \"x\")", result.GeneratedCode);
        Assert.DoesNotContain("Object.createInstance", result.GeneratedCode);
        Assert.DoesNotContain("arr.setValue", result.GeneratedCode);
    }
}
