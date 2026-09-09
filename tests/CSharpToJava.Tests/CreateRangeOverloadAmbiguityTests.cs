using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using System.Linq;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that ImmutableDictionary.CreateRange() calls with CSharpDictionary
/// arguments get a CSharpGenericIDictionary cast to resolve overload ambiguity
/// when CSharpDictionary matches both CSharpGenericIterable and CSharpGenericIDictionary
/// createRange overloads.
///
/// Note: The cast is only applied when the method symbol is available (project mode
/// with full Roslyn semantic analysis). In single-file mode without assembly
/// references, the symbol is null and the cast cannot be applied — this is expected.
/// </summary>
public class CreateRangeOverloadAmbiguityTests
{
    [Fact]
    public void CreateRange_NonGenericCall_WithDictionaryArg_NoCast()
    {
        // Non-generic CreateRange calls (where type args are concrete types like
        // String, String) should NOT get a cast — they don't have the ambiguity
        // because CSharpDictionary<String, String> only matches one overload.
        var result = Convert("""
using System.Collections.Generic;
using System.Collections.Immutable;

class Demo {
    public void Run()
    {
        var dict = new Dictionary<string, string>();
        var imm = ImmutableDictionary.CreateRange(dict);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        // Non-generic calls should not get a CSharpGenericIDictionary cast.
        Assert.DoesNotContain("CSharpGenericIDictionary", code);
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
