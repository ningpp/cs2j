using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# allows implicit string → ReadOnlySpan&lt;char&gt; conversion at call sites.
/// The converter must wrap string arguments with MemoryExtensions.asSpan(...)
/// when the target parameter is ReadOnlySpan&lt;char&gt;/Span&lt;char&gt;.
/// Regression: RegexCharClass.AddSet(ReadOnlySpan&lt;char&gt;) received a const
/// string without wrapping, producing "String cannot be converted to
/// ReadOnlySpan&lt;Character&gt;" in javac.
/// </summary>
public class StringArgumentToSpanParameterTests
{
    [Fact]
    public void ConstStringArgumentToReadOnlySpanParameter_IsWrappedWithAsSpan()
    {
        var result = Convert(@"
class CharClass
{
    private const string SetA = ""\u0030\u003A"";
    private const string SetB = ""\u0000\u0030\u003A"";

    private void AddSet(System.ReadOnlySpan<char> set)
    {
    }

    public void AddWord(bool ecma, bool negate)
    {
        if (ecma)
        {
            AddSet(negate ? SetA : SetB);
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MemoryExtensions.asSpan(", code);
        Assert.DoesNotContain("addSet((negate ? SetA : SetB))", code);
    }

    [Fact]
    public void StringVariableArgumentToReadOnlySpanParameter_IsWrappedWithAsSpan()
    {
        var result = Convert(@"
class CharClass
{
    private void AddSet(System.ReadOnlySpan<char> set)
    {
    }

    public void AddWord(bool ecma, string set)
    {
        if (ecma)
        {
            AddSet(set);
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MemoryExtensions.asSpan(set)", code);
    }

    [Fact]
    public void StringReturnedAsReadOnlySpan_IsWrappedWithAsSpan()
    {
        var result = Convert(@"
class CharClass
{
    private static System.ReadOnlySpan<char> GetSet(string s)
    {
        return s;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("return MemoryExtensions.asSpan(s);", code);
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
