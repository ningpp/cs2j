using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.IO;

namespace CSharpToJava.Tests;

/// <summary>
/// C# String.Format((IFormatProvider)CultureInfo.InvariantCulture, format, arg)
/// keeps the IFormatProvider static type through the cast (mvn 现场:
/// QUT.ShiftReduceParser charToString). compat StringHelper only had
/// (CultureInfo, String, Object...) / (Locale, ...) / (String, ...) forms, so a
/// single boxed argument did not bind. compat must expose the IFormatProvider
/// varargs overload and the converter must keep passing the provider first.
/// </summary>
public class StringFormatProviderOverloadTests
{
    [Fact]
    public void StringFormat_IFormatProviderCast_EmitsFormatCsWithProvider()
    {
        var result = Convert("""
using System;
using System.Globalization;

class C
{
    static string CharToString(char input)
    {
        return string.Format((IFormatProvider)CultureInfo.InvariantCulture, "'{0}'", (object)input);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(
            "StringHelper.formatCs((IFormatProvider)(CultureInfo.getInvariantCulture()), \"'{0}'\", (Object)(input))",
            code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatStringHelper_HasIFormatProviderFormatCs()
    {
        var compatPath = Path.Combine(
            FindRepoRoot(),
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "StringHelper.java");
        var source = File.ReadAllText(compatPath);

        // varargs makes the single-argument call bind; the overload must accept the
        // C# IFormatProvider parameter type, not only the compat CultureInfo.
        Assert.Contains(
            "public static String formatCs(IFormatProvider provider, String format, Object... args)",
            source, StringComparison.Ordinal);
        Assert.Contains("provider instanceof CultureInfo culture", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
