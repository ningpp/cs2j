using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Verifies that C# continue statements inside a plain switch (no goto case/default)
/// nested in a loop are preserved as a bare Java continue;.
/// Switch-with-goto-case behavior now belongs to GotoEliminator, which must run as a
/// preprocessing step before the Java pipeline; see GotoEliminatorTests for that coverage.
/// </summary>
public class ContinueInSwitchGotoCaseTests
{
    [Fact]
    public void Continue_InPlainSwitch_NoGotoCase_RemainsContinue()
    {
        // When there is no goto case/default, the switch does NOT use a while loop,
        // so a bare continue; is correct and should be preserved.
        var result = Convert(@"
class Test {
    void M(int x) {
        for (int i = 0; i < 10; i++) {
            switch (x) {
                case 1:
                    break;
                case 2:
                    continue;
                default:
                    break;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("continue;", result.GeneratedCode);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
