using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class NegatedOutConditionAssignmentTests
{
    [Fact]
    public void NegatedOutIf_ReadBackBeforeIf()
    {
        const string code = """
            class C {
                static bool TryGet(out int x) { x = 1; return true; }
                bool M() {
                    int v;
                    if (!TryGet(out v))
                        return false;
                    return v > 0;
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        var condPos = result.GeneratedCode.IndexOf("!tryGet(_vHolder1");
        var assignPos = result.GeneratedCode.IndexOf("v = _vHolder1", StringComparison.Ordinal);
        var ifPos = result.GeneratedCode.IndexOf("if (_ifCond", StringComparison.Ordinal);
        Assert.True(condPos >= 0, "expected lowered negated condition temp assignment");
        Assert.True(assignPos >= 0 && ifPos >= 0 && assignPos < ifPos,
            "out read-back should be emitted before negated if condition");
    }
}
