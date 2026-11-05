using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class LayoutAlgorithmSettingsStaticInitializerTests
{
    [Fact]
    public void StaticShowDebugCurvesField_GetsNoOpStaticInitializer()
    {
        var result = Convert("""
            namespace Microsoft.Msagl.DebugHelpers
            {
                public delegate void ShowDebugCurves(params DebugCurve[] curves);

                public class DebugCurve
                {
                }
            }

            namespace Microsoft.Msagl.Core.Layout
            {
                public abstract class LayoutAlgorithmSettings
                {
                    public static ShowDebugCurves ShowDebugCurves;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("ShowDebugCurves = shapes -> {};", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("showDebugCurves", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void LayoutAlgorithmSettingsWithoutStaticDelegateField_DoesNotInjectInitializer()
    {
        var result = Convert("""
            namespace Microsoft.Msagl.DebugHelpers
            {
                public delegate void ShowDebugCurves(params DebugCurve[] curves);

                public class DebugCurve
                {
                }
            }

            namespace Microsoft.Msagl.Core.Layout
            {
                public abstract class LayoutAlgorithmSettings
                {
                    public static int Count;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.DoesNotContain("= shapes -> {};", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
