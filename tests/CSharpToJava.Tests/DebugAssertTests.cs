using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that Debug.Assert / Trace.Assert / Contract.Requires are converted to
/// Java's built-in 'assert' statement keyword, not to a method call.
///
/// Bug fixed: Debug.Assert(cond) was erroneously converted to System.assertValue(cond)
/// due to the type being mapped to "System", "Assert" being camelCased to "assert",
/// and then "assert" being escaped (java keyword) to "assertValue".
/// </summary>
public class DebugAssertTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Debug.Assert ──────────────────────────────────────────────────────────

    [Fact]
    public void DebugAssert_SingleArg_EmitsJavaAssertStatement()
    {
        const string code = """
            using System.Diagnostics;
            class Sample
            {
                public void DebugAssert(int delta)
                {
                    Debug.Assert(delta >= 0);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.Contains("assert delta >= 0;", result.GeneratedCode);
        Assert.DoesNotContain("assertValue", result.GeneratedCode);
        Assert.DoesNotContain("System.assert", result.GeneratedCode);
    }

    [Fact]
    public void DebugAssert_TwoArgs_EmitsJavaAssertWithMessage()
    {
        const string code = """
            using System.Diagnostics;
            class Sample
            {
                public void Check(int x)
                {
                    Debug.Assert(x > 0, "x must be positive");
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.Contains("assert x > 0", result.GeneratedCode);
        Assert.Contains(": \"x must be positive\";", result.GeneratedCode);
        Assert.DoesNotContain("assertValue", result.GeneratedCode);
    }

    // ── Trace.Assert ─────────────────────────────────────────────────────────

    [Fact]
    public void TraceAssert_SingleArg_EmitsJavaAssertStatement()
    {
        const string code = """
            using System.Diagnostics;
            class Sample
            {
                public void Check(bool flag)
                {
                    Trace.Assert(flag);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.Contains("assert flag;", result.GeneratedCode);
        Assert.DoesNotContain("assertValue", result.GeneratedCode);
    }

    // ── Contract.Requires / Contract.Assert ───────────────────────────────────

    [Fact]
    public void ContractRequires_SingleArg_EmitsJavaAssertStatement()
    {
        const string code = """
            using System.Diagnostics.Contracts;
            class Sample
            {
                public void Process(object obj)
                {
                    Contract.Requires(obj != null);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.Contains("assert obj != null;", result.GeneratedCode);
        Assert.DoesNotContain("assertValue", result.GeneratedCode);
    }

    [Fact]
    public void ContractAssert_SingleArg_EmitsJavaAssertStatement()
    {
        const string code = """
            using System.Diagnostics.Contracts;
            class Sample
            {
                public void Process(int n)
                {
                    Contract.Assert(n >= 0);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        Assert.Contains("assert n >= 0;", result.GeneratedCode);
        Assert.DoesNotContain("assertValue", result.GeneratedCode);
    }
}
