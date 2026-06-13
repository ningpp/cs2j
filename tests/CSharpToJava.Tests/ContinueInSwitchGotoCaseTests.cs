using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# continue statements inside switch-with-goto-case are correctly
/// converted to exit the while loop (state = -1; break loopLabel) rather than
/// emitting a bare continue; which would incorrectly continue the while loop
/// instead of the enclosing for loop.
/// </summary>
public class ContinueInSwitchGotoCaseTests
{
    [Fact]
    public void Continue_InSwitchWithGotoCase_ConvertedToBreakLoop()
    {
        var result = Convert(@"
class Test {
    bool M(char c, int end) {
        int start = 0;
        for (int i = 0; i < end; i++) {
            switch (c) {
                case 'a':
                    start = i;
                    goto case 'b';
                case 'b':
                    start = i + 1;
                    break;
                case ']':
                    start = i;
                    i = end;
                    continue;
                default:
                    break;
            }
            start = 0;
        }
        return start > 0;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The switch-with-goto-case generates a while loop with a state variable.
        // A bare "continue;" inside that while loop would incorrectly re-enter the
        // while loop (simulating goto case), but the C# continue should exit the
        // switch and continue the enclosing for loop.
        // It should be converted to: _switchXState = -1; break _switchXLoop;
        Assert.DoesNotMatch(@"(?<!break\s)\bcontinue\s*;", result.GeneratedCode);
    }

    [Fact]
    public void Continue_InSwitchWithGotoCase_UsesStateAndBreakLoop()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        for (int i = 0; i < 10; i++) {
            switch (x) {
                case 1:
                    goto case 2;
                case 2:
                    break;
                case 3:
                    continue;
                default:
                    break;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The continue inside switch-with-goto-case should produce
        // state = -1; break loopLabel; pattern (same as break),
        // NOT a bare continue;
        Assert.DoesNotMatch(@"(?<!break\s)\bcontinue\s*;", result.GeneratedCode);
        // Should contain the state machine pattern for switch-with-goto-case
        Assert.Contains("_switch", result.GeneratedCode);
    }

    [Fact]
    public void Continue_InSwitchWithGotoDefault_ConvertedToBreakLoop()
    {
        var result = Convert(@"
class Test {
    void M(int x) {
        for (int i = 0; i < 10; i++) {
            switch (x) {
                case 1:
                    goto default;
                default:
                    break;
                case 2:
                    continue;
            }
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // continue inside switch-with-goto-default should also be converted
        Assert.DoesNotMatch(@"(?<!break\s)\bcontinue\s*;", result.GeneratedCode);
    }

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
