using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// File.Delete maps to compat FileHelper.delete, which keeps a checked IOException
/// declaration. Bare statement calls must be wrapped; a surrounding
/// try/catch (IOException) already handles it and stays unwrapped.
/// </summary>
public class FileDeleteCheckedExceptionTests
{
    [Fact]
    public void Delete_InsideIOExceptionCatch_IsNotWrapped()
    {
        var result = Convert("""
            using System;
            using System.IO;

            class Sample
            {
                public static void M(string p)
                {
                    try
                    {
                        File.Delete(p);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
            """);

        AssertConverted(result, "try {");
        Assert.Contains("FileHelper.delete(p);", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("catch (IOException", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UncheckedIOException", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_InFinallyWithoutIoCatch_IsWrappedWithUncheckedRethrow()
    {
        var result = Convert("""
            using System.IO;

            class Sample
            {
                public static void M(string p)
                {
                    try
                    {
                        File.WriteAllText(p, "x");
                    }
                    finally
                    {
                        File.Delete(p);
                    }
                }
            }
            """);

        AssertConverted(result, "try { FileHelper.delete(p); } "
            + "catch (java.io.IOException __cs2jIoEx) "
            + "{ throw new java.io.UncheckedIOException(__cs2jIoEx); }");
    }

    private static void AssertConverted(ConversionResult result, string expected)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.True(result.GeneratedCode.Contains(expected, StringComparison.Ordinal),
            $"Expected substring: {expected}\n---Generated---\n{result.GeneratedCode}");
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
