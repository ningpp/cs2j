using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that pointer post-increment (*ptr++) inside short-circuit (|| &amp;&amp;)
/// expressions preserves short-circuit semantics.
///
/// Bug: When *ptr++ appears inside a || expression like if (a || *ptr++ &lt; 0xA0),
/// the converter extracts the post-increment as a pre-statement that executes
/// unconditionally before the if, breaking short-circuit semantics. The increment
/// should only happen when earlier || operands evaluate to false.
/// </summary>
public class PointerPostIncrementShortCircuitTests
{
    [Fact]
    public void PostIncrementInsideOrShortCircuit_IncrementNotBeforeIf()
    {
        var result = Convert(@"
unsafe class Test {
    bool M(char* ptr, char* end) {
        if (ptr == end || *ptr++ < 0xA0)
        {
            return false;
        }
        return true;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The pointer increment (ptr = ptr.asSlice(2) or similar) must NOT appear
        // as a pre-statement before the if condition. If it does, it executes
        // unconditionally, breaking short-circuit semantics: when ptr == end is true,
        // *ptr++ should NOT be evaluated (and ptr should NOT be incremented).
        var ifIndex = result.GeneratedCode.IndexOf("if", StringComparison.Ordinal);
        Assert.True(ifIndex >= 0, "Expected generated code to contain 'if'");

        var codeBeforeIf = result.GeneratedCode[..ifIndex];
        // Look for the pointer reassignment pattern that would indicate the increment
        // was hoisted as a pre-statement (e.g., "ptr = ptr.asSlice(2)" or "ptr = ...asSlice...")
        var hasPreStatementIncrement = Regex.IsMatch(
            codeBeforeIf,
            @"ptr\s*=\s*.*asSlice\s*\(");
        Assert.False(hasPreStatementIncrement,
            "Pointer increment should NOT be hoisted as a pre-statement before the if, " +
            "as this breaks short-circuit semantics.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void PostIncrementInsideOrShortCircuit_IncrementInsideShortCircuitPath()
    {
        var result = Convert(@"
unsafe class Test {
    bool M(char* ptr, char* end) {
        if (ptr == end || *ptr++ < 0xA0)
        {
            return false;
        }
        return true;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The increment should appear somewhere in the generated code (inside the
        // short-circuit path), not be lost entirely.
        var hasIncrement = Regex.IsMatch(result.GeneratedCode, @"asSlice\s*\(");
        Assert.True(hasIncrement,
            "Expected generated code to contain pointer increment (asSlice) somewhere, " +
            "but it was not found.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void PostIncrementInsideAndShortCircuit_IncrementNotBeforeIf()
    {
        var result = Convert(@"
unsafe class Test {
    bool M(char* ptr, char* end) {
        if (ptr != end && *ptr++ >= 0xA0)
        {
            return true;
        }
        return false;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // Same issue with && short-circuit: the increment should not be hoisted
        // before the if, because when ptr == end, *ptr++ should not be evaluated.
        var ifIndex = result.GeneratedCode.IndexOf("if", StringComparison.Ordinal);
        Assert.True(ifIndex >= 0, "Expected generated code to contain 'if'");

        var codeBeforeIf = result.GeneratedCode[..ifIndex];
        var hasPreStatementIncrement = Regex.IsMatch(
            codeBeforeIf,
            @"ptr\s*=\s*.*asSlice\s*\(");
        Assert.False(hasPreStatementIncrement,
            "Pointer increment should NOT be hoisted as a pre-statement before the if, " +
            "as this breaks && short-circuit semantics.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void PostIncrementWithoutShortCircuit_PreStatementIsFine()
    {
        // When there is no short-circuit expression, extracting the post-increment
        // as a pre-statement is correct and should still work.
        var result = Convert(@"
unsafe class Test {
    char M(char* ptr) {
        char c = *ptr++;
        return c;
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // In this case, the pre-statement pattern is expected and correct.
        // The generated code should contain the pointer increment somewhere.
        var hasIncrement = Regex.IsMatch(result.GeneratedCode, @"asSlice\s*\(");
        Assert.True(hasIncrement,
            "Expected generated code to contain pointer increment (asSlice) for " +
            "non-short-circuit *ptr++.\n---Generated---\n" + result.GeneratedCode);
    }

    [Fact]
    public void PostIncrementInsideOrShortCircuit_WithFixedContext()
    {
        // Test with a fixed statement context, which is a more realistic scenario.
        var result = Convert(@"
unsafe class Test {
    bool M(char[] arr, int len) {
        fixed (char* ptr = arr)
        {
            char* end = ptr + len;
            if (ptr == end || *ptr++ < 0xA0)
            {
                return false;
            }
            return true;
        }
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);

        // The increment must not appear as a pre-statement before the if.
        var ifIndex = result.GeneratedCode.IndexOf("if", StringComparison.Ordinal);
        Assert.True(ifIndex >= 0, "Expected generated code to contain 'if'");

        var codeBeforeIf = result.GeneratedCode[..ifIndex];
        var hasPreStatementIncrement = Regex.IsMatch(
            codeBeforeIf,
            @"ptr\s*=\s*.*asSlice\s*\(");
        Assert.False(hasPreStatementIncrement,
            "Pointer increment should NOT be hoisted as a pre-statement before the if " +
            "in fixed context.\n---Generated---\n" + result.GeneratedCode);
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
