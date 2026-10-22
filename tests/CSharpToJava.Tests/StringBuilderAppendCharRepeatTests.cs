using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// StringBuilder.Append(char, int) (append a char repeatCount times) has no
/// java.lang.StringBuilder equivalent; the bridge must lower it to the compat
/// helper StringHelper.append(builder, c, count) by overload signature, not by
/// literal shape. Regression from dotnet-text-regularexpressions RegexRunner
/// stackDescription/textposDescription.
/// </summary>
public class StringBuilderAppendCharRepeatTests
{
    private static async Task<string> ConvertAsync(string source)
    {
        var results = await new ProjectConversionPipeline(new ConversionOptions())
            .ConvertProjectAsync(
                new[] { new SourceFile { FilePath = "Test.cs", Content = source } },
                projectName: "Demo");
        foreach (var failure in results.Where(r => !r.Success))
            throw new Xunit.Sdk.XunitException(
                string.Join("\n", failure.Diagnostics) + "\n---\n" + failure.GeneratedCode);
        return string.Join("\n", results.Select(r => r.GeneratedCode));
    }

    [Fact]
    public async Task AppendCharInt_LowersToCompatHelper()
    {
        var code = await ConvertAsync("""
            using System.Text;

            public class Test
            {
                public string Pad(StringBuilder sb)
                {
                    if (sb.Length < 8)
                    {
                        sb.Append(' ', 8 - sb.Length);
                    }
                    return sb.ToString();
                }
            }
            """);

        Assert.Contains("StringHelper.append(sb, ' ', 8 - sb.length())", code);
        // The invalid java overload must not be emitted.
        Assert.DoesNotContain("sb.append(' ', 8 - sb.length())", code);
    }

    [Fact]
    public async Task AppendCharInt_NonLiteralArguments_StillBridged()
    {
        var code = await ConvertAsync("""
            using System.Text;

            public class Test
            {
                public string Repeat(StringBuilder sb, char c, int n)
                {
                    sb.Append(c, n * 2);
                    return sb.ToString();
                }
            }
            """);

        Assert.Contains("StringHelper.append(sb, c, n * 2)", code);
    }

    [Fact]
    public async Task AppendSingleChar_KeepsPlainAppend()
    {
        var code = await ConvertAsync("""
            using System.Text;

            public class Test
            {
                public string Slash(StringBuilder sb)
                {
                    sb.Append('/');
                    return sb.ToString();
                }
            }
            """);

        Assert.Contains("sb.append('/')", code);
        Assert.DoesNotContain("StringHelper.append(sb, '/')", code);
    }

    [Fact]
    public async Task AppendStringInt_KeepsPlainAppend()
    {
        // Append(string, int) is a different overload (substring append);
        // the (char, int) bridge must not swallow it.
        var code = await ConvertAsync("""
            using System.Text;

            public class Test
            {
                public string Part(StringBuilder sb)
                {
                    sb.Append("abc", 1, 2);
                    return sb.ToString();
                }
            }
            """);

        Assert.DoesNotContain("StringHelper.append(sb, \"abc\"", code);
    }
}
