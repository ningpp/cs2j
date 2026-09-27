using CSharpToJava.Core.Context;
using CSharpToJava.Core.GotoEliminator;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// goto/label syntax must be removed by <see cref="GotoEliminator"/> before the Java
/// conversion pipeline runs. The Java statement transformer no longer contains its own
/// goto/label (state machine) support: encountering either in unpreprocessed source is
/// an error. These tests pin that contract and the intended end-to-end flow
/// (eliminate first, then convert).
/// </summary>
public class GotoRequiresPreprocessingTests
{
    [Fact]
    public void Goto_WithoutPreprocessing_FailsConversion()
    {
        var result = Convert("class Test { void M() { goto Done; Done: return; } }");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("GotoEliminator", StringComparison.Ordinal));
    }

    [Fact]
    public void Label_WithoutPreprocessing_FailsConversion()
    {
        var result = Convert("class Test { void M() { outer: for (int i = 0; i < 10; i++) { } } }");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("GotoEliminator", StringComparison.Ordinal));
    }

    [Fact]
    public void GotoCase_WithoutPreprocessing_FailsConversion()
    {
        var result = Convert("class Test { void M(int x) { switch (x) { case 1: goto case 2; case 2: break; } } }");

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            d => d.Message.Contains("GotoEliminator", StringComparison.Ordinal));
    }

    [Fact]
    public void GotoSource_AfterElimination_ConvertsSuccessfully()
    {
        const string source = "class Test { void M() { goto Done; Done: return; } }";

        var eliminated = new GotoEliminator().Eliminate(source);
        Assert.True(eliminated.Changed);
        Assert.DoesNotContain("goto", eliminated.OutputCode, StringComparison.Ordinal);

        var result = Convert(eliminated.OutputCode);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
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
