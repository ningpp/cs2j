using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MSTestThrowsExceptionConversionTests
{
    [Fact]
    public void ThrowsException_GenericTypeArgument_BecomesClassLiteral()
    {
        var result = Convert("""
            using System.IO;
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class Sample
            {
                void M(string missingFile)
                {
                    Assert.ThrowsException<FileNotFoundException>(() => Parse(missingFile));
                }

                static object Parse(string path) => null;
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(
            "Assert.throwsException(FileNotFoundException.class,",
            result.GeneratedCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Assert.throwsException((Supplier<Object>)",
            result.GeneratedCode,
            StringComparison.Ordinal);
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
