using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// A target-typed conditional whose result is ReadOnlySpan&lt;char&gt; must have its
/// string branches wrapped with MemoryExtensions.asSpan(...). C# implicitly converts
/// string to ReadOnlySpan&lt;char&gt;, but Java requires the explicit wrap.
/// Regression: RegexCharClass.SetFromProperty returns
///   !invert ? set : (set[0] == '\0' ? set.AsSpan(1) : NullCharString + set).
/// </summary>
public class ConditionalSpanBranchAdaptationTests
{
    [Fact]
    public void TargetTypedConditionalToReadOnlySpan_WrapsStringBranches()
    {
        var result = Convert(@"
using System;
class CharClass
{
    private const string NullCharString = ""\0"";

    static System.ReadOnlySpan<char> SetFromProperty(string set, bool invert)
    {
        return
            !invert ? set :
            set[0] == '\0' ? set.AsSpan(1) :
            NullCharString + set;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("MemoryExtensions.asSpan(set)", code);
        Assert.Contains("MemoryExtensions.asSpan(NullCharString + set)", code);
        Assert.DoesNotContain("return (!invert ? set :", code);
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
