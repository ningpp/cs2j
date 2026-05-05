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
