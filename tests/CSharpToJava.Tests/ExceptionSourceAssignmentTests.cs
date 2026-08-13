using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ExceptionSourceAssignmentTests
{
    [Fact]
    public void ExceptionSourceAssignment_MapsToExceptionCompat()
    {
        var result = Convert(@"
using System;

public class Sample
{
    public static Exception Create(string message)
    {
        Exception ex = new InvalidOperationException(message);
        ex.Source = ""dotnet.text.json.Rethrowable"";
        return ex;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        Assert.Contains("ExceptionCompat.setSource(ex, \"dotnet.text.json.Rethrowable\")", code, StringComparison.Ordinal);
        Assert.Contains("import io.github.ningpp.compat.ExceptionCompat", code, StringComparison.Ordinal);
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
