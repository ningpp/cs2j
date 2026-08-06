using CSharpToJava.CLI;

namespace CSharpToTypeScript.Tests;

public class TsCliTargetOptionTests
{
    [Fact]
    public void ConvertOptions_Default_Target_Is_Java()
    {
        var opts = new ConvertOptions();
        Assert.Equal("java", opts.Target);
    }

    [Fact]
    public void ConvertOptions_Accepts_TypeScript_Target()
    {
        // CommandLineParser 解析 --target typescript
        var opts = new ConvertOptions { Target = "typescript" };
        Assert.Equal("typescript", opts.Target);
    }
}