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

    // ── Bug 4 ──────────────────────────────────────────────────────────────────
    // When the same local variable is passed as ref to two consecutive calls in the
    // same method body, the converter previously emitted:
    //   - DoubleHolder _d3Ref = new DoubleHolder(d3)  ← TWICE (duplicate Java decl)
    //   - d3 = _d3Ref.value                           ← TWICE (duplicate writeback)
    // Fix: the second ref arg hit reuses the already-active holder via
    // TryGetActiveRefHolder, producing no new pre/post statements.

    [Fact]
    public void RefArg_SameVarPassedRefTwiceConsecutively_NoDuplicateDeclaration()
    {
        const string code = """
            class Sample
            {
                public double RefMethod1(ref double d1)
                {
                    d1 = 1.1;
                    return d1;
                }

                public double RefMethod3(double d3)
                {
                    double returnValue1 = RefMethod1(ref d3);
                    double returnValue2 = RefMethod1(ref d3);
                    return returnValue1 + returnValue2;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Holder must be declared exactly once
        var holderDecl = "DoubleHolder _d3Ref = new DoubleHolder(d3)";
        var declCount = CountOccurrences(result.GeneratedCode, holderDecl);
        Assert.True(declCount == 1,
            $"Expected exactly 1 occurrence of '{holderDecl}', found {declCount}.\n\n{result.GeneratedCode}");

        // Both calls must pass the same holder
        Assert.Contains("refMethod1(_d3Ref)", result.GeneratedCode);

        // Writeback must appear exactly once
        var writeback = "d3 = _d3Ref.value";
        var writebackCount = CountOccurrences(result.GeneratedCode, writeback);
        Assert.True(writebackCount == 1,
            $"Expected exactly 1 occurrence of '{writeback}', found {writebackCount}.\n\n{result.GeneratedCode}");
    }

    // ── out parameter forwarding (mirrors Bug 1 for ref) ────────────────────
    // OutMethod1 has an out DoubleHolder d1 and calls OutMethod2(out d1).
    // The argument must be the holder "d1" itself, NOT emit a new wrapper.

    [Fact]
    public void OutParam_ForwardedToAnotherOutMethod_PassesHolder()
    {
        const string code = """
            class Sample
            {
                public double OutMethod2(out double d2)
                {
                    d2 = 3.14;
                    return d2;
                }

                public double OutMethod1(out double d1)
                {
                    return OutMethod2(out d1);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // The forwarded out argument must be the raw holder, not a new wrapper.
        Assert.Contains("outMethod2(d1)", result.GeneratedCode);
        Assert.DoesNotContain("_d1Holder", result.GeneratedCode);
    }

    // ── out full round-trip with plain-param caller ────────────────────────
    // OutMethod3 takes a plain double d3 and calls OutMethod2(out d3).
    // d3 must be wrapped in a holder, and the return statement must be split.

    [Fact]
    public void OutParam_FullRoundTrip_ThreeMethods()
    {
        const string code = """
            class Sample
            {
                public double OutMethod1(out double d1)
                {
                    return OutMethod2(out d1);
                }

                public double OutMethod2(out double d2)
                {
                    d2 = 3.14;
                    return d2;
                }

                public double OutMethod3(double d3)
                {
                    return OutMethod2(out d3);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // OutMethod1 — forwards its holder directly, no extra wrapper
        Assert.Contains("outMethod2(d1)", result.GeneratedCode);
        Assert.DoesNotContain("_d1Holder", result.GeneratedCode);

        // OutMethod2 — body: assign & return via holder
        Assert.Contains("d2.value = 3.14", result.GeneratedCode);
        Assert.Contains("return d2.value", result.GeneratedCode);

        // OutMethod3 — wraps local double d3, splits return
        Assert.Contains("DoubleHolder _d3Holder = new DoubleHolder()", result.GeneratedCode);
        Assert.Contains("outMethod2(_d3Holder)", result.GeneratedCode);
        Assert.Contains("d3 = _d3Holder.value", result.GeneratedCode);
        Assert.Contains("return _ret", result.GeneratedCode);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(pattern, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += pattern.Length;
        }
        return count;
    }
}
