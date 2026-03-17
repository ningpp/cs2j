using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Func&lt;...&gt; type mapping and delegate-invocation rewriting.
///
/// Bugs fixed:
///   1. Func&lt;T,R&gt; was emitting Vavr's Function1&lt;&gt; instead of java.util.function.Function&lt;&gt;.
///   2. Bare delegate invocations (sequence(m)) were not rewritten to sequence.apply(m).
/// </summary>
public class FuncDelegateInvocationTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug 1a ─────────────────────────────────────────────────────────────────
    // Func<int, double> must map to java.util.function.Function<Integer, Double>,
    // NOT to io.vavr.Function1<Integer, Double>.

    [Fact]
    public void Func2_MapsToStandardJavaFunction()
    {
        const string code = """
            class UnimodalSequence
            {
                System.Func<int, double> sequence;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Function<Integer, Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function1", result.GeneratedCode);
        Assert.DoesNotContain("io.vavr", result.GeneratedCode);
    }

    // ── Bug 1b ─────────────────────────────────────────────────────────────────
    // Func<double> (0-input, 1-output) must map to java.util.function.Supplier<Double>.

    [Fact]
    public void Func1_MapsToSupplier()
    {
        const string code = """
            class Example
            {
                System.Func<double> factory;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("Supplier<Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function0", result.GeneratedCode);
    }

    // ── Bug 1c ─────────────────────────────────────────────────────────────────
    // Func<int, int, double> (2-input) must map to java.util.function.BiFunction<Integer, Integer, Double>.

    [Fact]
    public void Func3_MapsToBiFunction()
    {
        const string code = """
            class Example
            {
                System.Func<int, int, double> combine;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("BiFunction<Integer, Integer, Double>", result.GeneratedCode);
        Assert.DoesNotContain("Function2", result.GeneratedCode);
    }

    // ── Bug 2a ─────────────────────────────────────────────────────────────────
    // Invoking a Func<int, double> field as sequence(m) must emit sequence.apply(m).

    [Fact]
    public void Func2_BareDelegateInvocation_EmitsApply()
    {
        const string code = """
            class UnimodalSequence
            {
                System.Func<int, double> sequence;

                public double FindSequenceScore(int m)
                {
                    return sequence(m);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("sequence.apply(m)", result.GeneratedCode);
        // Must NOT appear as a bare call
        Assert.DoesNotContain("return sequence(m)", result.GeneratedCode);
    }

    // ── Bug 2b ─────────────────────────────────────────────────────────────────
    // Invoking a Func<double> (Supplier) field as factory() must emit factory.get().

    [Fact]
    public void Func1_BareDelegateInvocation_EmitsGet()
    {
        const string code = """
            class Example
            {
                System.Func<double> factory;

                public double GetValue()
                {
                    return factory();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("factory.get()", result.GeneratedCode);
        Assert.DoesNotContain("return factory()", result.GeneratedCode);
    }

    // ── Bug 3 ──────────────────────────────────────────────────────────────────
    // When Sequence is a Func<int,double> *property*, calling Sequence(m) must
    // emit getSequence().apply(m), not Sequence.apply(m).

    [Fact]
    public void Func2_PropertyDelegateInvocation_EmitsGetterThenApply()
    {
        const string code = """
            class UnimodalSequenceProperty
            {
                System.Func<int, double> sequence;

                internal System.Func<int, double> Sequence
                {
                    get { return sequence; }
                    set { sequence = value; }
                }

                public double FindSequenceScore(int m)
                {
                    return Sequence(m);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Property getter must be called first, then .apply() on the result.
        Assert.Contains("getSequence().apply(m)", result.GeneratedCode);
        // Must NOT appear as a bare property call or a plain method call.
        Assert.DoesNotContain("Sequence(m)", result.GeneratedCode);
        Assert.DoesNotContain("Sequence.apply", result.GeneratedCode);
    }

    // ── Bug 4 ──────────────────────────────────────────────────────────────────
    // Invoking a Func<int, double> field via member access (this.sequence(i)) must
    // emit this.sequence.apply(i), not this.sequence(i).
    // Reproduces the reported bug:
    //   return delegate (int i) { return Math.Min(this.sequence(i), 3.14); };

    [Fact]
    public void Func2_MemberAccessDelegateInvocation_EmitsApply()
    {
        const string code = """
            class UnimodalSequence
            {
                System.Func<int, double> sequence;

                System.Func<int, double> GetForMinimum(System.Func<int, double> seq)
                {
                    return delegate (int i) { return System.Math.Min(this.sequence(i), 3.14); };
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // this.sequence(i) must be rewritten to this.sequence.apply(i)
        Assert.Contains("this.sequence.apply(i)", result.GeneratedCode);
        // Must NOT appear as a bare member invocation
        Assert.DoesNotContain("this.sequence(i)", result.GeneratedCode);
    }
}
