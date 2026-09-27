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
