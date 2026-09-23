using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StringBuilderConversionTests
{
    [Fact]
    public void Convert_StringBuilder_AppendCharArray_StartCount_PreservesOffsetLenSemantics()
    {
        var result = Convert(@"
using System.Text;

class Test
{
    string M(char[] buf, int start, int len)
    {
        var sb = new StringBuilder();
        sb.Append(buf, start, len);
        return sb.ToString();
    }
}");

        Assert.True(result.Success);
        // C# Append(char[], int startIndex, int count) → Java append(char[], int offset, int len)
        // Both use (offset, count/len) — NO transformation needed.
        // The generated code should be sb.append(buf, start, len) NOT sb.append(buf, start, start + len)
        Assert.Contains("sb.append(buf, start, len);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("start + len", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_NewString_CharArray_StartCount_PreservesOffsetCountSemantics()
    {
        var result = Convert(@"
class Test
{
    string M(char[] buf, int start, int len)
    {
        return new string(buf, start, len);
    }
}");

        Assert.True(result.Success);
        // C# new string(char[], int startIndex, int count) → Java new String(char[], int offset, int count)
        // Both use (offset, count) — NO transformation needed.
        Assert.Contains("new String(buf, start, len)", result.GeneratedCode, StringComparison.Ordinal);
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
