using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that catch clauses are reordered so that project-specific exceptions
/// (which extend RuntimeException in Java) precede the System.Exception catch-all
/// that maps to RuntimeException.
/// </summary>
public class ExceptionCatchOrderTests
{
    [Fact]
    public void CustomExceptionCatch_PrecedesRuntimeExceptionCatch()
    {
        var result = Convert(@"
using System;

public class MyException : Exception { }

public class Test
{
    public void M()
    {
        try { }
        catch (MyException e) { }
        catch (Exception e) { }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        int customIndex = result.GeneratedCode.IndexOf("catch (MyException", StringComparison.Ordinal);
        int runtimeIndex = result.GeneratedCode.IndexOf("catch (RuntimeException", StringComparison.Ordinal);

        Assert.True(customIndex >= 0, "Expected catch (MyException) in generated code.\n" + result.GeneratedCode);
        Assert.True(runtimeIndex >= 0, "Expected catch (RuntimeException) in generated code.\n" + result.GeneratedCode);
        Assert.True(customIndex < runtimeIndex,
            "Project-specific exception catch must precede RuntimeException catch.\n" + result.GeneratedCode);
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
