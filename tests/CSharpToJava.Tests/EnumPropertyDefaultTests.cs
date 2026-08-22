using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression (AGL SolverShellTests): C# enum auto-properties default to the
/// member with value 0, but Java enum fields default to null. The generated
/// backing field must be initialized to the 0-valued member.
/// </summary>
public class EnumPropertyDefaultTests
{
    [Fact]
    public void AutoProperty_EnumType_BackingFieldDefaultsToZeroMember()
    {
        var result = Convert("""
            enum SolverAlgorithm { ProjectOnly, QpscWithScaling, QpscWithoutScaling }

            class Solution
            {
                public SolverAlgorithm AlgorithmUsed { get; set; }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("private SolverAlgorithm algorithmUsed = SolverAlgorithm.ProjectOnly;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoProperty_EnumType_ExplicitZeroValueMember_Used()
    {
        var result = Convert("""
            enum Color { Red = 5, Green = 0, Blue = 2 }

            class Sample
            {
                public Color DefaultColor { get; set; }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("private Color defaultColor = Color.Green;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AutoProperty_FlagsEnum_NoDefaultInitializer()
    {
        var result = Convert("""
            [System.Flags]
            enum Options { None = 0, A = 1, B = 2 }

            class Sample
            {
                public Options Flags { get; set; }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("private int flags;", result.GeneratedCode, StringComparison.Ordinal);
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
