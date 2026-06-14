using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SwitchUnreachableBreakTests
{
    [Fact]
    public void PlainSwitch_ReturnWithTrailingBreak_StripsBreak()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                return 1;
                break;
            case 1:
                return 2;
                break;
            default:
                return -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("return 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return 2;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("return -1;", result.GeneratedCode, StringComparison.Ordinal);
        // After fix: break should be stripped after terminal return
        Assert.DoesNotContain("break;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_ThrowWithTrailingBreak_StripsBreak()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                throw new Exception(""zero"");
                break;
            default:
                return 1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("throw new RuntimeException", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("break;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_NormalBreakOnly_Kept()
    {
        var result = Convert(@"
class C {
    void Test(int x) {
        int y = 0;
        switch (x) {
            case 0:
                y = 1;
                break;
            default:
                y = -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("y = 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("break;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_ReturnThenMoreCode_StripsEverythingAfterReturn()
    {
        var result = Convert(@"
class C {
    int Test(int x) {
        switch (x) {
            case 0:
                return 1;
                System.Console.WriteLine(""never"");
                break;
            default:
                return -1;
                break;
        }
    }
}");
        Assert.True(result.Success);
        Assert.Contains("return 1;", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("System.out.println(\"never\")", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PlainSwitch_PreservesExplicitCaseBlocks_ForLocalVariableScope()
    {
        var result = Convert(@"
class C {
    int Test(string x) {
        int total = 0;
        switch (x) {
            case ""a"":
                {
                    int value = 1;
                    total += value;
                    break;
                }
            case ""b"":
                {
                    int value = 2;
                    total += value;
                    break;
                }
        }
        return total;
    }
}");
        Assert.True(result.Success);
        var code = result.GeneratedCode.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("case \"a\":", code, StringComparison.Ordinal);
        Assert.Matches("case \"a\":\\s*\\{\\s*int value_1 = 1;\\s*total \\+= value_1;", code);
        Assert.Matches("case \"b\":\\s*\\{\\s*int value_1 = 2;\\s*total \\+= value_1;", code);
    }

    [Fact]
    public void PlainSwitch_OutArgumentInExpression_DeclaresHolderBeforeSwitch()
    {
        var result = Convert(@"
enum EntityType { Unexpanded, Expanded }

class C {
    EntityType Read(out int i) {
        i = 1;
        return EntityType.Unexpanded;
    }

    int Test() {
        int i;
        switch (Read(out i)) {
            case EntityType.Unexpanded:
                return i;
            default:
                return 0;
        }
    }
}");

        Assert.True(result.Success, result.GeneratedCode);
        var code = result.GeneratedCode.Replace("\r\n", "\n", StringComparison.Ordinal);
        var holderIndex = code.IndexOf("IntHolder _iHolder", StringComparison.Ordinal);
        var callIndex = code.IndexOf("var _switchExpr", StringComparison.Ordinal);
        var readBackIndex = code.IndexOf("i = _iHolder", StringComparison.Ordinal);
        var switchIndex = code.IndexOf("switch (_switchExpr", StringComparison.Ordinal);

        Assert.True(holderIndex >= 0, result.GeneratedCode);
        Assert.True(callIndex >= 0, result.GeneratedCode);
        Assert.True(readBackIndex >= 0, result.GeneratedCode);
        Assert.True(switchIndex >= 0, result.GeneratedCode);
        Assert.True(holderIndex < callIndex, result.GeneratedCode);
        Assert.True(callIndex < readBackIndex, result.GeneratedCode);
        Assert.True(readBackIndex < switchIndex, result.GeneratedCode);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
