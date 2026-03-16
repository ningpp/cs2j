using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for C# ref/out parameter conversion to the Java holder-object pattern.
///
/// Bugs fixed:
///   1. ref-parameter re-forwarded as a ref arg emitted "d2.value" instead of "d2" (holder unwrapped).
///   2. Local variable passed as ref was not wrapped in a holder (type mismatch at call site).
///   3. TransformReturnStatement never drained pre/post statements; holder setup was silently dropped
///      when the ref call appeared directly in a return statement.
/// </summary>
public class RefParameterTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug 1 ──────────────────────────────────────────────────────────────────
    // refMethod2 has a DoubleHolder parameter d2 and calls refMethod1(ref d2).
    // The argument must be the holder "d2" itself, NOT "d2.value".

    [Fact]
    public void RefParam_ForwardedToAnotherRefMethod_PassesHolder()
    {
        const string code = """
            class Sample
            {
                public double RefMethod1(ref double d1)
                {
                    d1 = 1.1;
                    return d1;
                }

                public double RefMethod2(ref double d2)
                {
                    return RefMethod1(ref d2);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // The forwarded argument must be the raw holder, not .value
        Assert.Contains("refMethod1(d2)", result.GeneratedCode);
        Assert.DoesNotContain("refMethod1(d2.value)", result.GeneratedCode);
    }

    // ── Bug 2 ──────────────────────────────────────────────────────────────────
    // refMethodCall() has a local double d2 and calls refMethod2(ref d2) as an
    // expression statement. The call site must wrap d2 in a DoubleHolder and
    // write the mutated value back afterward.

    [Fact]
    public void RefArg_LocalVarInExpressionStatement_WrapsInHolder()
    {
        const string code = """
            class Sample
            {
                public double RefMethod2(ref double d2) { return d2; }

                public void RefMethodCallVoid()
                {
                    double d2 = 3.14;
                    RefMethod2(ref d2);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Holder must be created, initialized with the current value, and written back.
        Assert.Contains("DoubleHolder _d2Ref = new DoubleHolder(d2)", result.GeneratedCode);
        Assert.Contains("refMethod2(_d2Ref)", result.GeneratedCode);
        Assert.Contains("d2 = _d2Ref.value", result.GeneratedCode);
    }

    // ── Bug 3 ──────────────────────────────────────────────────────────────────
    // When the ref call is directly inside a return statement, pre/post statements
    // were silently dropped. The return must be split: capture result in _ret,
    // emit write-back, then return _ret.

    [Fact]
    public void RefArg_LocalVarInReturnStatement_EmitsHolderAndWriteback()
    {
        const string code = """
            class Sample
            {
                public double RefMethod2(ref double d2) { return d2; }

                public double RefMethodCall()
                {
                    double d2 = 3.14;
                    return RefMethod2(ref d2);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Holder must be created before the call.
        Assert.Contains("DoubleHolder _d2Ref = new DoubleHolder(d2)", result.GeneratedCode);
        // Return must be split: capture, write-back, then return.
        Assert.Contains("_ret = refMethod2(_d2Ref)", result.GeneratedCode);
        Assert.Contains("d2 = _d2Ref.value", result.GeneratedCode);
        Assert.Contains("return _ret", result.GeneratedCode);
        // Must NOT have "return refMethod2(...)" directly (that would skip write-back).
        Assert.DoesNotContain("return refMethod2(d2)", result.GeneratedCode);
    }

    // ── Full round-trip ────────────────────────────────────────────────────────
    // All three methods from the original bug report together.

    [Fact]
    public void RefParam_FullRoundTrip_ThreeMethods()
    {
        const string code = """
            class Sample
            {
                public double RefMethod1(ref double d1)
                {
                    d1 = 1.1;
                    return d1;
                }

                public double RefMethod2(ref double d2)
                {
                    return RefMethod1(ref d2);
                }

                public double RefMethodCall()
                {
                    double d2 = 3.14;
                    return RefMethod2(ref d2);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // refMethod1 — body: assign & return via holder
        Assert.Contains("d1.value = 1.1", result.GeneratedCode);
        Assert.Contains("return d1.value", result.GeneratedCode);

        // refMethod2 — Bug 1: pass holder d2, not d2.value
        Assert.Contains("refMethod1(d2)", result.GeneratedCode);
        Assert.DoesNotContain("refMethod1(d2.value)", result.GeneratedCode);

        // refMethodCall — Bug 2+3: wrap local, split return
        Assert.Contains("DoubleHolder _d2Ref = new DoubleHolder(d2)", result.GeneratedCode);
        Assert.Contains("refMethod2(_d2Ref)", result.GeneratedCode);
        Assert.Contains("d2 = _d2Ref.value", result.GeneratedCode);
        Assert.Contains("return _ret", result.GeneratedCode);
    }
}
