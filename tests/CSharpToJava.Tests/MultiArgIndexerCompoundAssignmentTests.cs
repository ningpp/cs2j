using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Compound assignment (op=) on a multi-argument indexer must expand to a
/// set(args, get(args) op rhs) call, because Java forbids compound assignment
/// on a method-call result (e.g. PlaneTransformation[0, 2] += x).
/// </summary>
public class MultiArgIndexerCompoundAssignmentTests
{
    [Fact]
    public void CompoundAssignmentOnMultiArgIndexer_ExpandsToSetGet()
    {
        var source = @"
class Matrix
{
    private double[,] elements = new double[3, 3];
    public double this[int row, int col]
    {
        get { return elements[row, col]; }
        set { elements[row, col] = value; }
    }
    public Matrix Transform { get { return this; } }
    public void Pan(double x, double y)
    {
        Transform[0, 2] += x;
        Transform[1, 2] -= y;
    }
}";
        var result = Convert(source);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("getTransform().set(0, 2, getTransform().get(0, 2) + x)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("getTransform().set(1, 2, getTransform().get(1, 2) - y)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("get(0, 2) += x", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Matrix.cs",
            Options = new ConversionOptions(),
        });
    }
}
