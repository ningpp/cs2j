using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that local variables captured by lambdas and externally reassigned
/// are wrapped in array holders to satisfy Java's effectively-final constraint.
/// </summary>
public class LambdaExternalReassignmentTests
{
    /// <summary>
    /// Variable captured by lambda and reassigned AFTER the lambda declaration.
    /// </summary>
    [Fact]
    public void CapturedVariable_ReassignedAfterLambda_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = () => Console.WriteLine(x);
        x = 2;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _x", result.GeneratedCode);
        Assert.Contains("_x[0]", result.GeneratedCode);
    }

    /// <summary>
    /// Variable captured by lambda and reassigned BEFORE the lambda declaration.
    /// </summary>
    [Fact]
    public void CapturedVariable_ReassignedBeforeLambda_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        x = 2;
        Action a = () => Console.WriteLine(x);
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _x", result.GeneratedCode);
        Assert.Contains("_x[0]", result.GeneratedCode);
    }

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
    /// Variable mutated INSIDE lambda (existing GetMutatedCaptures path) still works.
    /// </summary>
    [Fact]
    public void CapturedVariable_MutatedInsideLambda_ExistingPathWorks()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        act();
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("_count", result.GeneratedCode);
        Assert.Contains("[0]", result.GeneratedCode);
    }

    /// <summary>
    /// Variable both externally reassigned AND internally mutated.
    /// The holder should be created once (by pre-scan), not duplicated.
    /// </summary>
    [Fact]
    public void CapturedVariable_BothExternalAndInternalMutation_SingleHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int count = 0;
        Action act = () => { count++; };
        count = 10;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var holderCount = CountOccurrences(result.GeneratedCode, "int[] _count");
        Assert.Equal(1, holderCount);
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
    /// Multiple captured variables, only some are externally reassigned.
    /// </summary>
    [Fact]
    public void MultipleCaptures_OnlyReassignedGetHolders()
    {
        var result = Convert(@"
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
        Assert.Contains("int[] _x", result.GeneratedCode);
        Assert.DoesNotContain("int[] _y", result.GeneratedCode);
    }

    /// <summary>
    /// Compound assignment (x += 2) is also detected as external reassignment.
    /// </summary>
    [Fact]
    public void CapturedVariable_CompoundAssignment_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = () => Console.WriteLine(x);
        x += 2;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _x", result.GeneratedCode);
        Assert.Contains("_x[0]", result.GeneratedCode);
    }

    /// <summary>
    /// For-loop variable captured by lambda — the increment (i++) is external to the lambda
    /// but inside the for-statement. TransformForStatement now detects the pending holder
    /// registered by PreScanLambdaCaptures and emits the holder array as part of the
    /// for-loop initializer, so the captured variable is effectively final in the lambda.
    /// </summary>
    [Fact]
    public void ForLoopVariable_CapturedByLambda_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        for (int i = 0; i < 10; i++) {
            Action a = () => Console.WriteLine(i);
        }
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _i", result.GeneratedCode);
        Assert.Contains("_i[0]", result.GeneratedCode);
    }

    /// <summary>
    /// Anonymous method (delegate { ... }) capturing an externally-reassigned variable.
    /// </summary>
    [Fact]
    public void AnonymousMethod_CapturedReassignedVariable_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = delegate { Console.WriteLine(x); };
        x = 2;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _x", result.GeneratedCode);
        Assert.Contains("_x[0]", result.GeneratedCode);
    }

    /// <summary>
    /// Multiple lambdas capturing the same externally-reassigned variable — single holder.
    /// </summary>
    [Fact]
    public void MultipleLambdas_CaptureSameReassignedVariable_SingleHolder()
    {
        var result = Convert(@"
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
        var holderCount = CountOccurrences(result.GeneratedCode, "int[] _x");
        Assert.Equal(1, holderCount);
    }

    /// <summary>
    /// Post-increment operator on captured variable is detected as external reassignment.
    /// </summary>
    [Fact]
    public void CapturedVariable_PostIncrement_ProducesHolder()
    {
        var result = Convert(@"
using System;
class T {
    void M() {
        int x = 1;
        Action a = () => Console.WriteLine(x);
        x++;
    }
}");
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("int[] _x", result.GeneratedCode);
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
