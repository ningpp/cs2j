using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that C# primitive numeric CompareTo calls map to the correct Java
/// static comparison helper. double.CompareTo(double) must become
/// Double.compare(double, double), not Integer.compare(int, int).
/// </summary>
public class PrimitiveCompareToMappingTests
{
    [Fact]
    public void DoubleCompareTo_UsesDoubleCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(double x, double y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Double.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FloatCompareTo_UsesFloatCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(float x, float y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Float.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IntCompareTo_UsesIntegerCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(int x, int y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Integer.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LongCompareTo_UsesLongCompare()
    {
        var result = Convert(@"
class Test {
    int Compare(long x, long y) => x.CompareTo(y);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("Long.compare(x, y)", result.GeneratedCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression: a non-primitive property/field receiver must not be mistaken for a
    /// primitive just because another class in the same compilation declares a primitive
    /// FIELD with the same name. C# `this.Left.CompareTo(other.Left)` where Left is a
    /// non-primitive (Variable) must emit `this.getLeft().compareTo(other.getLeft())`,
    /// NOT `Double.compare(this.getLeft(), other.getLeft())`. The cross-type collision
    /// happened because TryDetectPrimitiveByFieldDeclaration searched every syntax tree
    /// in the compilation by field name only.
    /// </summary>
    [Fact]
    public void CompareTo_NonPrimitiveReceiver_DoesNotCollideWithSameNamedPrimitiveFieldInAnotherType()
    {
        var result = Convert(@"
using System;

class Variable : IComparable<Variable> {
    public int CompareTo(Variable other) => 0;
}

class CollidingClass {
    public double Left;
}

class Constraint : IComparable<Constraint> {
    public Variable Left { get; private set; }
    public Variable Right { get; private set; }
    public double Gap { get; private set; }

    public int CompareTo(Constraint other) {
        int cmp = this.Left.CompareTo(other.Left);
        if (0 == cmp) cmp = this.Right.CompareTo(other.Right);
        if (0 == cmp) cmp = this.Gap.CompareTo(other.Gap);
        return cmp;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        // Non-primitive receivers must use instance .compareTo(...), NOT Double.compare(...)
        Assert.Contains("getLeft().compareTo(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getRight().compareTo(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Double.compare(this.getLeft()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Double.compare(this.getRight()", result.GeneratedCode, StringComparison.Ordinal);
        // The genuine double property (Gap) must still use Double.compare(...)
        Assert.Contains("Double.compare(this.getGap()", result.GeneratedCode, StringComparison.Ordinal);
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
