using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SwitchUnreachableBreakCleanupTests
{
    [Fact]
    public void SwitchCase_ReturnThenBreak_DropsTrailingBreak()
    {
        const string code = """
            public class C {
                int M(int x) {
                    switch (x) {
                        case 1:
                            return 42;
                            break;
                        default:
                            return 0;
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("return 42;", result.GeneratedCode);
        Assert.DoesNotContain("return 42;\n            break;", result.GeneratedCode);
    }
}
