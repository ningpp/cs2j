using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for extension method support:
/// - Declaration side: extension methods are kept as static methods in their host class
/// - Call side: extension method calls are lowered to static calls (HostClass.method(receiver, args...))
/// - Edge cases: generics, named arguments, overloads, using aliases
/// </summary>
public class ExtensionMethodTests
{
    /// <summary>
    /// Basic extension method declaration should keep the receiver parameter
    /// and output as a static method in the host class.
    /// </summary>
    [Fact]
    public void Declaration_KeepsReceiverAsFirstParameter()
    {
        var result = Convert(@"
public static class StringExtensions
{
    public static string Repeat(this string s, int count)
    {
        return string.Join("""", Enumerable.Repeat(s, count));
    }
}");

        Assert.True(result.Success);
        // The 'this string s' parameter should remain as the first parameter (without 'this')
        Assert.Contains("static String repeat(String s, int count)", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
