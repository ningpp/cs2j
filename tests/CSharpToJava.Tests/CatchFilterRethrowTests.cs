using CSharpToJava.Core.Context;
using CSharpToJava.Core.PatternDesugar;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# catch filters only enter the handler when the predicate is true; when it is
/// false the exception must keep propagating. Java has no catch filters, so the
/// generated catch must rethrow the original exception in the else branch.
/// Regression: JsonConverter&lt;T&gt;.WriteCore swallowed InvalidOperationException
/// and fell through to AssertionError("unreachable").
/// </summary>
public class CatchFilterRethrowTests
{
    [Fact]
    public void CatchFilterFalse_RethrowsOriginalException()
    {
        var result = Convert(@"
using System;

class C
{
    bool M()
    {
        try
        {
            throw new InvalidOperationException(""boom"");
        }
        catch (InvalidOperationException ex) when (ex.Message == ""expected"")
        {
            Console.WriteLine(""caught"");
            return true;
        }
    }
}
");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The catch clause keeps its declaration, so the rethrow targets the
        // original variable; the main if/else renderer always wraps else bodies in
        // a multiline block, so assert the semantic form rather than exact spacing.
        Assert.Contains("throw ex;", code, StringComparison.Ordinal);
        Assert.Contains("else", code, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new AssertionError(\"unreachable\")", code, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var desugared = new PatternDesugarPreprocessor().Preprocess(sourceCode);
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = desugared.OutputCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
