using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.Threading.Timeout.InfiniteTimeSpan and
/// System.Runtime.RuntimeFeature map to compat classes (type mappings with
/// imports). Reproduces Regex.java: "找不到符号 变量 RuntimeFeature" — a
/// short-name static receiver resolved through a using directive landed on the
/// compat getter (RuntimeFeature.getIsDynamicCodeCompiled()) but the required
/// import was never emitted. The fix lives in MapStaticTypeReceiver, which maps
/// the static receiver type but previously skipped the TypeMappings imports.
/// The unit-test single-file pipeline cannot resolve these APIs the way the real
/// project does, so the property-import behaviour is exercised through the
/// project-level pipeline; the mapping configuration itself is asserted directly.
/// </summary>
public class TimeoutAndRuntimeFeatureMappingTests
{
    [Fact]
    public void TimeoutInfiniteTimeSpan_MapsToCompatWithImport()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
class Holder
{
    public static readonly System.TimeSpan InfiniteMatchTimeout = System.Threading.Timeout.InfiniteTimeSpan;
}",
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.DoesNotContain("System.Threading", code);
        Assert.Contains("import io.github.ningpp.compat.Timeout", code);
        Assert.Contains("Timeout.InfiniteTimeSpan", code);
    }

    [Fact]
    public async Task RuntimeFeatureStaticProperty_EmitsCompatImport()
    {
        const string source = @"
using System.Runtime.CompilerServices;

public class FeatureGate
{
    public static bool IsCompiled()
    {
        return RuntimeFeature.IsDynamicCodeCompiled;
    }
}";
        var tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2j-rf-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(tempDir, "FeatureGate.cs"), source);

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                tempDir, new ConversionOptions(), projectName: "RuntimeFeatureTest");

            var result = Assert.Single(results, r => r.FileName != null && r.FileName.Contains("FeatureGate"));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
            var code = result.GeneratedCode ?? "";

            // The static receiver must resolve to the compat class AND carry its import.
            Assert.Contains("import io.github.ningpp.compat.RuntimeFeature", code);
            Assert.DoesNotContain("System.Runtime.RuntimeFeature", code);
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeFeatureMappingEntry_PointsToCompatClass()
    {
        // Verify the mapping configuration that drives the real conversion.
        var registry = new CSharpToJava.TypeMapping.TypeMappingRegistry();

        Assert.Equal("RuntimeFeature", registry.MapType("System.Runtime.CompilerServices.RuntimeFeature"));
        Assert.Contains("io.github.ningpp.compat.RuntimeFeature", registry.GetRequiredImports("System.Runtime.CompilerServices.RuntimeFeature"));
        Assert.Equal("Timeout", registry.MapType("System.Threading.Timeout"));
        Assert.Contains("io.github.ningpp.compat.Timeout", registry.GetRequiredImports("System.Threading.Timeout"));
    }
}
