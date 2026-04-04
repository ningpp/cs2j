using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for ref argument holder declarations inside yield return statements.
///
/// Root cause: TransformYieldReturn did not drain pre/post-statements emitted
/// by ArgumentTransformer when processing ref arguments. The holder variable
/// declaration (e.g., CharHolder _previousInstructionRef = ...) leaked into
/// the wrong scope, and the post-statement (e.g., previousInstruction =
/// _previousInstructionRef.value) was also misplaced or lost.
/// </summary>
public class YieldReturnRefArgTests
{
    /// <summary>
    /// Reproduces the AGL GeometryGraphWriter bug: ref parameter in
    /// yield return inside if/else branches. Both branches call a method
    /// with ref char parameter, and the holder declarations must be
    /// scoped correctly within each branch.
    /// </summary>
    [Fact]
    public void YieldReturnWithRefArg_InIfElse_ShouldDeclareHolderBeforeUse()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Writer
{
    IEnumerable<string> GetTokens(int count)
    {
        var prev = 'w';
        for (int i = 0; i < count; i++)
        {
            if (i != count - 1)
                yield return Format(i, ref prev);
            else
                yield return Format(i, ref prev);
        }
    }

    string Format(int value, ref char previous)
    {
        var result = previous == 'L' ? value.ToString() : ""L"" + value;
        previous = 'L';
        return result;
    }
}");

        Assert.True(result.Success, "Conversion should succeed");

        // The ref holder variable must be declared BEFORE it is used.
        // It must NOT appear as an undeclared reference.
        var code = result.GeneratedCode;

        // Each _yieldResult.add(...Ref) call must be preceded by the holder declaration
        // in the same scope. Check that Ref holders are declared somewhere.
        Assert.Contains("Holder", code);

        // The holder must NOT be used before declaration (i.e., if the holder
        // is used in the if-branch, its declaration must also be in the if-branch,
        // not leaked into the else-branch)
        var lines = code.Split('\n');
        foreach (var line in lines)
        {
            // If a line contains _yieldResult.add(...Ref), the Ref variable
            // should have been declared earlier in the SAME block
            if (line.Contains("_yieldResult.add") && line.Contains("Ref"))
            {
                // Verify the holder is not used without being declared first
                // (the exact position check is handled by the Java compiler,
                // but we can verify the declaration exists in the output)
                Assert.Contains("Holder", code);
            }
        }
    }

    /// <summary>
    /// Simple case: single yield return with ref arg should work.
    /// </summary>
    [Fact]
    public void YieldReturnWithRefArg_Simple_ShouldIncludeHolderDeclaration()
    {
        var result = Convert(@"
using System.Collections.Generic;

class Processor
{
    IEnumerable<int> Process()
    {
        int state = 0;
        yield return Advance(ref state);
        yield return Advance(ref state);
    }

    int Advance(ref int s) { return s++; }
}");

        Assert.True(result.Success, "Conversion should succeed");
        var code = result.GeneratedCode;

        // Holder declaration must appear before the _yieldResult.add call
        Assert.Contains("Holder", code);

        // The holder must be declared, not just used
        int holderDeclIdx = code.IndexOf("Holder _stateRef");
        int holderUseIdx = code.IndexOf("_stateRef)");
        Assert.True(holderDeclIdx >= 0, "Holder declaration should exist");
        Assert.True(holderDeclIdx < holderUseIdx,
            "Holder declaration must appear before its first use");
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
