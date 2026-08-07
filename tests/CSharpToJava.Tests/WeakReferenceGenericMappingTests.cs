using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that System.WeakReference&lt;T&gt; maps to the compat generic WeakReference class
/// with tryGetTarget/setTarget members. Reproduces Regex._replref:
/// javac "type io.github.ningpp.compat.WeakReference does not take parameters".
/// </summary>
public class WeakReferenceGenericMappingTests
{
    [Fact]
    public void WeakReferenceOfT_MapsToCompatGenericType_WithMembers()
    {
        var result = Convert(@"
class Cache {
    private System.WeakReference<string> _ref = new System.WeakReference<string>(""x"");

    public bool TryGet(out string value) => _ref.TryGetTarget(out value);

    public void Store(string value) => _ref.SetTarget(value);
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("WeakReference<String>", code);
        Assert.Contains("import io.github.ningpp.compat.WeakReference", code);
        Assert.Contains("tryGetTarget", code);
        Assert.Contains("setTarget", code);
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
