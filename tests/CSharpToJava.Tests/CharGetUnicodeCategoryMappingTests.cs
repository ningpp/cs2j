using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# char.GetUnicodeCategory(char) (static method on System.Char) must map to the
/// compat CharUnicodeInfo.getUnicodeCategory(char) helper. Without a config entry
/// the generic camelCase rule emitted Character.getUnicodeCategory, which does not
/// exist on java.lang.Character.
/// Regression: RegexCharClass.CharInCategory uses char.GetUnicodeCategory(ch).
/// </summary>
public class CharGetUnicodeCategoryMappingTests
{
    [Fact]
    public void CharGetUnicodeCategory_MapsToCharUnicodeInfoHelper()
    {
        var result = Convert(@"
using System;
using System.Globalization;
class C
{
    static bool IsCategory(char ch)
    {
        UnicodeCategory cat = char.GetUnicodeCategory(ch);
        return cat == UnicodeCategory.UppercaseLetter;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("CharUnicodeInfo.getUnicodeCategory(ch)", code);
        Assert.DoesNotContain("Character.getUnicodeCategory", code);
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
