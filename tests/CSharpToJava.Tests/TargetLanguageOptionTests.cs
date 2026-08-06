using CSharpToJava.Core.Context;

namespace CSharpToJava.Tests;

public class TargetLanguageOptionTests
{
    [Fact]
    public void Default_TargetLanguage_Is_Java()
    {
        var opts = new ConversionOptions();
        Assert.Equal(TargetLanguage.Java, opts.TargetLanguage);
    }

    [Fact]
    public void TargetLanguage_Can_Be_Set_To_TypeScript()
    {
        var opts = new ConversionOptions { TargetLanguage = TargetLanguage.TypeScript };
        Assert.Equal(TargetLanguage.TypeScript, opts.TargetLanguage);
    }
}
