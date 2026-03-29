using CSharpToJava.Core.Pipeline;
using System.Linq;

namespace CSharpToJava.Tests;

public class ListRemoveAutoboxingTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void ListOfInt_Remove_WrapsWithIntegerValueOf()
    {
        const string code = """
            using System.Collections.Generic;

            class Foo
            {
                void Bar()
                {
                    var list = new List<int> { 1, 2, 3 };
                    int val = 2;
                    list.Remove(val);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Integer.valueOf(val)", result.GeneratedCode);
    }

    [Fact]
    public void ListOfString_Remove_NoAutoboxing()
    {
        const string code = """
            using System.Collections.Generic;

            class Foo
            {
                void Bar()
                {
                    var list = new List<string> { "a", "b" };
                    list.Remove("a");
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("Integer.valueOf", result.GeneratedCode);
        Assert.Contains(".remove(\"a\")", result.GeneratedCode);
    }
}
