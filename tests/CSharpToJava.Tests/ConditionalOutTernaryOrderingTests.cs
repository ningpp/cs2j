using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConditionalOutTernaryOrderingTests
{
    [Fact]
    public void TernaryConditionWithOut_ReadBackHappensBeforeBranchUse()
    {
        const string code = """
            class C {
                static bool GetPair(out int a, out int b) { a = 1; b = 2; return true; }

                string M() {
                    int a, b;
                    return GetPair(out a, out b) ? ("" + (a + b)) : "";
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("_aHolder", result.GeneratedCode);
        Assert.Contains("_bHolder", result.GeneratedCode);
        Assert.DoesNotContain("(a + b)", result.GeneratedCode);
    }

    [Fact]
    public void TernaryTryGetValueOutVar_UsesHolderValueInsideBranches()
    {
        const string code = """
            using System.Collections.Generic;

            class C {
                readonly Dictionary<int, string> map = new();

                string? M(int key) {
                    return map.TryGetValue(key, out var v) ? v : null;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("_vHolder", result.GeneratedCode);
        Assert.DoesNotContain("? v :", result.GeneratedCode);
        Assert.Contains("? _vHolder.value :", result.GeneratedCode);
    }
}
