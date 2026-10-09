using CSharpToJava.Core.ClosureLift;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for lambda capture lowering.
/// M5 (C45) retired the converter's own holder pre-scan: externally
/// reassigned captured variables are now lowered exclusively by the
/// closure-lift preprocessor (display classes), so those cases run
/// through the preprocess → convert chain. Read-only captures never
/// needed a holder and keep converting directly.
/// </summary>
public class LambdaExternalReassignmentTests
{
    /// <summary>
    /// Variable captured by lambda but NOT reassigned — no holder needed.
    /// </summary>
    [Fact]
    public void CapturedVariable_NotReassigned_NoHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 5;
        Action a = () => Console.WriteLine(x);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("int[] _x", result.GeneratedCode);
    }

    /// <summary>
    /// Variable mutated INSIDE lambda: while the converter still owns the
    /// residual array-holder fallback (removed in the next M5 step), the
    /// closure-lift chain lowers it to a display class field.
    /// </summary>
    [Fact]
    public void CapturedVariable_MutatedInsideLambda_ChainLiftsToDisplayClass()
    {
        var result = ConvertAfterClosureLift(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        act();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Cs2jDisplayClass", result.GeneratedCode);
        Assert.Contains("_cs2jCap_count", result.GeneratedCode);
        Assert.DoesNotContain("int[] _count", result.GeneratedCode);
    }

    /// <summary>
    /// Variable both externally reassigned AND internally mutated:
    /// a single display class owns the shared mutable slot.
    /// </summary>
    [Fact]
    public void CapturedVariable_BothExternalAndInternalMutation_SingleDisplayClass()
    {
        var result = ConvertAfterClosureLift(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        count = 10;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "class Cs2jDisplayClass"));
        Assert.DoesNotContain("int[] _count", result.GeneratedCode);
    }

    /// <summary>
    /// Variable not captured by any lambda — reassignment is irrelevant.
    /// </summary>
    [Fact]
    public void NonCapturedVariable_Reassigned_NoHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        x = 2;
        Console.WriteLine(x);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.DoesNotContain("int[] _x", result.GeneratedCode);
    }

    /// <summary>
    /// Multiple captured variables, only some are externally reassigned:
    /// only the reassigned one becomes a display class field.
    /// </summary>
    [Fact]
    public void MultipleCaptures_OnlyReassignedGetLifted()
    {
        var result = ConvertAfterClosureLift(@"
using System;
class T {
    void M() {
        int x = 1;
        int y = 2;
        Action a = () => Console.WriteLine(x + y);
        x = 3;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // Display-class consolidation (design D3) shares one class across all
        // captures, read-only ones included; the retired behavior was emitting
        // an array holder only for the reassigned variable.
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "class Cs2jDisplayClass"));
        Assert.Contains("_cs2jCap_x", result.GeneratedCode);
        Assert.DoesNotContain("int[] _x", result.GeneratedCode);
        Assert.DoesNotContain("int[] _y", result.GeneratedCode);
    }

    /// <summary>
    /// Multiple lambdas capturing the same externally-reassigned variable
    /// share one display class.
    /// </summary>
    [Fact]
    public void MultipleLambdas_CaptureSameReassignedVariable_SingleDisplayClass()
    {
        var result = ConvertAfterClosureLift(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = () => Console.WriteLine(x);
        Action b = () => Console.WriteLine(x + 1);
        x = 2;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Equal(1, CountOccurrences(result.GeneratedCode, "class Cs2jDisplayClass"));
        Assert.DoesNotContain("int[] _x", result.GeneratedCode);
    }

    /// <summary>
    /// Post-increment operator on a captured variable is detected as an external write.
    /// </summary>
    [Fact]
    public void CapturedVariable_PostIncrement_LiftedToDisplayClass()
    {
        var result = ConvertAfterClosureLift(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = () => Console.WriteLine(x);
        x++;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("_cs2jCap_x", result.GeneratedCode);
        Assert.DoesNotContain("int[] _x", result.GeneratedCode);
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

    /// <summary>
    /// Runs the closure-lift preprocessor (the M5 single source of truth
    /// for mutable captures) and then converts the rewritten C# to Java.
    /// </summary>
    private static ConversionResult ConvertAfterClosureLift(string sourceCode)
    {
        var lift = new ClosureLiftPreprocessor().Preprocess(sourceCode);
        Assert.True(lift.Changed, string.Join("\n", lift.Diagnostics.Select(d => d.ToString())));

        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = lift.OutputCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static int CountOccurrences(string source, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
