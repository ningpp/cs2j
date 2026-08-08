using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Static, non-capturing local functions declared after a return statement must be
/// hoisted to class-level private methods (like other local functions).
/// Regression: RegexCompiler.NodeSupportsNonBacktrackingImplementation was dropped,
/// leaving a call to nodeSupportsNonBacktrackingImplementation without a declaration.
/// </summary>
public class LocalFunctionAfterReturnHoistingTests
{
    [Fact]
    public void StaticLocalFunctionDeclaredAfterReturn_IsHoisted()
    {
        var result = Convert(@"
class C
{
    bool TryGo(int node)
    {
        if (node > 0)
        {
            return Supports(node);
        }
        return true;

        static bool Supports(int n)
        {
            return n > 0;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("static boolean supports(", code);
        Assert.DoesNotContain("TODO: LocalFunctionStatement", code);
    }

    [Fact]
    public void StaticLocalFunctionReferencingConstLocal_IsHoisted()
    {
        // Regression: RegexCompiler.NodeSupportsNonBacktrackingImplementation is a
        // static local function that references the const local HasCapturesFlag;
        // const locals must not count as captures (they can be inlined).
        var result = Convert(@"
class C
{
    bool TryGo(int node)
    {
        const int HasCapturesFlag = (int)(1 << 31);
        if (node > 0)
        {
            return Supports(node);
        }
        return true;

        static bool Supports(int n)
        {
            return (n & HasCapturesFlag) == 0 && n > 0;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("static boolean supports(", code);
        Assert.DoesNotContain("TODO: LocalFunctionStatement", code);
    }

    [Fact]
    public void CallToCapturingLocalFunction_BecomesTodoComment()
    {
        var result = Convert(@"
class C
{
    int Go(int x)
    {
        int baseValue = 10;
        return Helper() + baseValue;

        int Helper()
        {
            return baseValue + 1;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        Assert.Contains("TODO: call to capturing local function Helper", code);
        Assert.DoesNotContain("helper()", code);
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
