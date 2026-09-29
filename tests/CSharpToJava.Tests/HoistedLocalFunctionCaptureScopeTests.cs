using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that hoisted local function capture registrations are scoped to the
/// enclosing method. MethodConversionState.Reset() failed to clear the
/// capture/default dictionaries, so a local function named e.g. "Add" that
/// captures a variable leaked into every later method: unrelated calls to a
/// method named "Add" had the stale captured variable appended as an argument
/// (add(5, converters) instead of add(5)), producing javac "cannot find
/// symbol" errors (IEnumerableDefaultConverter/DictionaryDefaultConverter).
/// </summary>
public class HoistedLocalFunctionCaptureScopeTests
{

    private static string ExtractMethodBody(string code, string methodName)
    {
        int idx = code.IndexOf($"private void {methodName}(", System.StringComparison.Ordinal);
        Assert.True(idx >= 0, $"method {methodName} not found in:\n{code}");
        int open = code.IndexOf('{', idx);
        int close = code.IndexOf('}', open);
        return code.Substring(open, close - open);
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
