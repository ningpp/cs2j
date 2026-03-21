using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Dictionary.TryGetValue() conversion.
///
/// Bugs fixed:
///   1. TryGetValue(k, out existingVar)  — the already-declared variable case incorrectly
///      fell through to the generic out-parameter path, which injected an ObjectHolder and
///      produced the invalid call  dictionary.get(1, _d1Holder).
///      Fix: StatementTransformer.TransformIfStatement Fix 5 now also handles
///      IdentifierNameSyntax (existing variable), emitting
///      existingVar = dict.get(key) inside the containsKey branch.
/// </summary>
public class DictionaryTryGetValueTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── out var (new declaration) — regression guard ──────────────────────────

    [Fact]
    public void TryGetValue_OutVar_UsesContainsKeyAndGet()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    var dictionary = new Dictionary<int, string>();
                    if (dictionary.TryGetValue(1, out var value))
                    {
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("containsKey(1)", result.GeneratedCode);
        Assert.Contains(".get(1)", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("_valueHolder", result.GeneratedCode);
    }

    // ── out existingVar (pre-declared variable) — bug fix ────────────────────

    [Fact]
    public void TryGetValue_OutExistingVar_UsesContainsKeyAndAssignment()
    {
        const string code = """
            using System.Collections.Generic;
            class Point { }
            class C
            {
                public void DicTryGetValue()
                {
                    Point d1;
                    Dictionary<int, Point> dictionary = new Dictionary<int, Point>();
                    if (dictionary.TryGetValue(1, out d1))
                    {
                        System.Console.WriteLine(d1);
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must use containsKey as the guard
        Assert.Contains("containsKey(1)", result.GeneratedCode);
        // Must assign from get() inside the body (not pass _holder to get)
        Assert.Contains("d1 = dictionary.get(1)", result.GeneratedCode);
        // Must NOT produce ObjectHolder or pass _d1Holder to get()
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("_d1Holder", result.GeneratedCode);
        // get() must be called with exactly one argument (key only)
        Assert.DoesNotContain("get(1, ", result.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_OutExistingVar_WithElse_BothBranchesGenerated()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    string result;
                    var dict = new Dictionary<int, string>();
                    if (dict.TryGetValue(42, out result))
                    {
                        System.Console.WriteLine(result);
                    }
                    else
                    {
                        System.Console.WriteLine("not found");
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("containsKey(42)", result.GeneratedCode);
        Assert.Contains("result = dict.get(42)", result.GeneratedCode);
        Assert.Contains("not found", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("get(42, ", result.GeneratedCode);
    }
}
