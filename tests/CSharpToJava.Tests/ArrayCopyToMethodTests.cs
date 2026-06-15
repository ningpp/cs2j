using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ArrayCopyToMethodTests
{
    [Fact]
    public void Array_CopyTo_OnPrimitiveArray_UsesSystemArraycopy()
    {
        var result = Convert("""
class Program
{
    void Copy(double[] source, double[] dest)
    {
        source.CopyTo(dest, 1);
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains(
            "System.arraycopy(source, 0, dest, 1, source.length);",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".copyTo(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpArray.of(dest)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_CopyTo_OnPrimitiveArrayElement_UsesSystemArraycopy()
    {
        var result = Convert("""
class Program
{
    void CopyRows(double[][] source)
    {
        double[][] dest = new double[source.Length][];
        for (int i = 0; i < source.Length; i++)
        {
            dest[i] = new double[source[i].Length];
            source[i].CopyTo(dest[i], 0);
        }
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        Assert.Contains(
            "System.arraycopy(source[i], 0, dest[i], 0, source[i].length);",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".copyTo(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("CSharpArray.of(dest[i])", result.GeneratedCode, StringComparison.Ordinal);
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
