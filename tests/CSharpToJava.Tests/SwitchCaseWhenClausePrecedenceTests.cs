using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Iteration 23 regression: switch `case X when A || B:` conditions are emitted as
/// `Objects.equals(expr, X) && A || B` — Java's && binds tighter than ||, so the
/// when-clause grouping is lost. Regression: RegexNode.canBeMadeAtomic
/// `case Boundary when node.Str == WordClass || node.Str == DigitClass` matched
/// unconditionally when the string was DigitClass, wrongly auto-atomicizing \d*
/// in `(\w[bcd]\d*)*fg` (PatternsReduceDifferently[48]).
/// </summary>
public class SwitchCaseWhenClausePrecedenceTests
{
    [Fact]
    public void CaseWhen_WithOrClause_IsParenthesized()
    {
        var result = Convert("""
class C {
    static bool M(int type, string str) {
        switch (type) {
            case 1 when str == "A" || str == "B":
                return true;
            default:
                return false;
        }
    }
}
""");

        var code = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.equals(type, 1) && (Objects.equals(str, \"A\") || Objects.equals(str, \"B\"))", code);
    }

    [Fact]
    public void CaseWhen_WithAndClause_KeepsSingleParens()
    {
        var result = Convert("""
class C {
    static bool M(int type, int m, int ch) {
        switch (type) {
            case 1 when m > 0 && ch != 5:
                return true;
            default:
                return false;
        }
    }
}
""");

        var code = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Objects.equals(type, 1) && (m > 0 && ch != 5)", code);
    }

    [Fact]
    public void UintSubtractionComparison_StaysUnsigned()
    {
        // RegexCharClass.IsECMAWordChar: (((uint)ch - 'A') & ~0x20) < 26 — the uint
        // subtraction wraps, and the comparison must stay unsigned. The semantic model
        // sees a long (uint op char promotes to long), so the emitted mask pattern
        // must trigger the compareUnsigned path. '@' (0x40) - 'A' wraps to a huge value.
        var result = Convert("""
class C {
    static bool M(char ch) {
        return (((uint)ch - 'A') & ~0x20) < 26;
    }
}
""");

        var code = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Integer.compareUnsigned(", code);
    }

    [Fact]
    public void UlongComparison_UsesLongCompareUnsigned()
    {
        // Regex.ValidateMatchTimeout: (ulong)(ticks - 1) >= MaximumMatchTimeoutTicks —
        // negative ticks must compare as a huge unsigned value and throw.
        var result = Convert("""
class C {
    static bool M(long ticks, long max) {
        return (ulong)(ticks - 1) >= (ulong)max;
    }
}
""");

        var code = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Long.compareUnsigned(", code);
    }

    [Fact]
    public void InterpolatedString_EscapedQuote_EmitsPlainQuote()
    {
        var result = Convert("""
class C {
    static string M(object m) { return $"Match: \"{m}\""; }
}
""");

        var code = result.GeneratedCode ?? "";
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Match: \\\"", code);
        Assert.DoesNotContain("\\\\\\\"", code);
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
