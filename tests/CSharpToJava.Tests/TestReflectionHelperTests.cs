using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Test-module reflection batch:
/// 1. Overrides of "protected internal" base methods must widen to public
///    (base "protected internal abstract" maps to public in Java).
/// 2. Type.GenericTypeArguments → TypeHelper.getGenericTypeArguments.
/// 3. proxy.MakeGenericType(args) on a variable receiver → TypeHelper.makeGenericType.
/// Regression: tests/TestUtilities + PrecompiledRegexScenarioTest.
/// </summary>
public class TestReflectionHelperTests
{
    [Fact]
    public void OverrideOfProtectedInternalBaseMethod_WidensToPublic()
    {
        var result = Convert(@"
class Base
{
    protected internal abstract int CreateInstance();
}
class Derived : Base
{
    protected override int CreateInstance() { return 1; }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("public abstract int createInstance();", code);
        Assert.Contains("public int createInstance()", code);
        Assert.DoesNotContain("protected int createInstance()", code);
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
