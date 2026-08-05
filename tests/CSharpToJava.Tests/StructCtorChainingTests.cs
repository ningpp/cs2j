using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: a readonly struct whose parameterized constructor chains via
/// ': this(...)' and whose target constructor chains via ': this()' must not
/// lose the chained argument assignments (e.g. BorderInfo(double innerMargin)).
/// </summary>
public class StructCtorChainingTests
{
    [Fact]
    public void ReadonlyStruct_ThisChainWithThisZeroTarget_KeepsParameterAssignment()
    {
        var pipeline = new ProjectConversionPipeline(new ConversionOptions());
        var results = pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "BorderInfo.cs",
                Content = """
public readonly struct BorderInfo
{
    public double InnerMargin { get; }
    public double FixedPosition { get; }
    public double Weight { get { return this.borderWeight; } }
    readonly double borderWeight;
    public static double NoFixedPosition { get { return double.NaN; } }
    public static double DefaultFreeWeight { get { return 1.0; } }
    public BorderInfo(double innerMargin) : this(innerMargin, NoFixedPosition, DefaultFreeWeight) { }
    public BorderInfo(double innerMargin, double fixedPosition, double weight) : this()
    {
        this.InnerMargin = innerMargin;
        this.FixedPosition = fixedPosition;
        this.borderWeight = weight;
    }
}
""",
            },
        }).Result;

        var sample = results.FirstOrDefault(r => r.FileName != null);
        Assert.NotNull(sample);
        Assert.True(sample.Success, string.Join("\n", sample.Diagnostics.Select(d => d.Message)) + "\n---Generated---\n" + sample.GeneratedCode);

        // Scope the check to the 1-arg ctor block: it must propagate the margin
        // parameter into the fields instead of only default-initializing them.
        const string oneArgStart = "public BorderInfo(double innerMargin) {";
        const string threeArgStart = "public BorderInfo(double innerMargin, double fixedPosition, double weight) {";
        int start = sample.GeneratedCode.IndexOf(oneArgStart, StringComparison.Ordinal);
        int end = sample.GeneratedCode.IndexOf(threeArgStart, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "1-arg ctor block not found:\n" + sample.GeneratedCode);
        var oneArgCtor = sample.GeneratedCode.Substring(start, end - start);
        Assert.Contains("this.innerMargin = innerMargin;", oneArgCtor, StringComparison.Ordinal);
        Assert.Contains("this.fixedPosition = getNoFixedPosition();", oneArgCtor, StringComparison.Ordinal);
        Assert.Contains("this.borderWeight = getDefaultFreeWeight();", oneArgCtor, StringComparison.Ordinal);
    }
}
