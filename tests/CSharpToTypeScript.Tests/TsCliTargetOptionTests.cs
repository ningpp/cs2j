using CommandLine;
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
        // 直接属性赋值验证（真实命令行解析见 ConvertOptions_Parses_Dash_T_Typescript）
        var opts = new ConvertOptions { Target = "typescript" };
        Assert.Equal("typescript", opts.Target);
    }

    [Fact]
    public void ConvertOptions_Parses_Dash_T_Typescript()
    {
        // CommandLineParser 真实解析 -t/--target typescript
        // ConvertOptions.Input 为 Required，需连同 -i 一起解析
        var result = Parser.Default.ParseArguments<ConvertOptions>(new[] { "-i", "sample.cs", "-t", "typescript" });
        Assert.Equal(ParserResultType.Parsed, result.Tag);
        Assert.Equal("typescript", ((Parsed<ConvertOptions>)result).Value.Target);
    }
}
