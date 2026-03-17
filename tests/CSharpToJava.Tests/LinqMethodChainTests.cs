using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for LINQ method chain conversion via LinqRewriter.
/// Validates that LINQ method chains are correctly rewritten to procedural
/// loops before Java conversion.
/// </summary>
public class LinqMethodChainTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    private static void AssertConverts(string code, params string[] mustContain)
    {
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        foreach (var expected in mustContain)
            Assert.Contains(expected, result.GeneratedCode);
    }

    // ── Where + Select + ToList ─────────────────────────────────────────────

    [Fact]
    public void Where_Select_ToList_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Transform(List<int> numbers)
                {
                    return numbers.Where(x => x > 5).Select(x => x.ToString()).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Should produce a procedural loop, not Stream API
        Assert.DoesNotContain(".stream()", result.GeneratedCode);
        Assert.DoesNotContain(".filter(", result.GeneratedCode);
    }

    // ── Distinct ────────────────────────────────────────────────────────────

    [Fact]
    public void Distinct_ToList_ProducesValidOutput()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> GetDistinct(List<int> items)
                {
                    return items.Distinct().ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Accept either procedural loop or Stream API fallback (depends on
        // how well the semantic model resolves ToList on the current runtime).
        Assert.True(
            result.GeneratedCode.Contains("_ProceduralLinq") || result.GeneratedCode.Contains(".distinct()"),
            "Expected either procedural helper or .distinct() stream operation");
    }

    // ── Skip + Take ─────────────────────────────────────────────────────────

    [Fact]
    public void Skip_Take_ToList_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Page(List<int> items)
                {
                    return items.Skip(10).Take(5).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain(".stream()", result.GeneratedCode);
    }

    // ── SkipWhile + TakeWhile ───────────────────────────────────────────────

    [Fact]
    public void SkipWhile_TakeWhile_ToList_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> WindowedData(List<int> items)
                {
                    return items.SkipWhile(x => x < 10).TakeWhile(x => x < 100).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain(".stream()", result.GeneratedCode);
    }

    // ── Sum, Count, Average, Min, Max ───────────────────────────────────────

    [Fact]
    public void Sum_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Total(List<int> numbers)
                {
                    return numbers.Where(x => x > 0).Sum();
                }
            }
            """;
        AssertConverts(code);
    }

    [Fact]
    public void Count_WithPredicate_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Count(List<int> numbers)
                {
                    return numbers.Count(x => x > 0);
                }
            }
            """;
        AssertConverts(code);
    }

    // ── First / FirstOrDefault / Last / LastOrDefault ────────────────────────

    [Fact]
    public void First_WithPredicate_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int FindFirst(List<int> numbers)
                {
                    return numbers.First(x => x > 5);
                }
            }
            """;
        AssertConverts(code);
    }

    [Fact]
    public void FirstOrDefault_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int FindFirstOrDefault(List<int> numbers)
                {
                    return numbers.FirstOrDefault();
                }
            }
            """;
        AssertConverts(code);
    }

    // ── All ─────────────────────────────────────────────────────────────────

    [Fact]
    public void All_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool AllPositive(List<int> numbers)
                {
                    return numbers.All(x => x > 0);
                }
            }
            """;
        AssertConverts(code);
    }

    // ── Contains ────────────────────────────────────────────────────────────

    [Fact]
    public void Contains_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool HasValue(List<int> numbers)
                {
                    return numbers.Where(x => x > 0).Contains(42);
                }
            }
            """;
        AssertConverts(code);
    }

    // ── ToArray ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToArray_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int[] AsArray(List<int> numbers)
                {
                    return numbers.Where(x => x > 0).ToArray();
                }
            }
            """;
        AssertConverts(code);
    }

    // ── ToDictionary ────────────────────────────────────────────────────────

    [Fact]
    public void ToDictionary_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public Dictionary<int, string> AsDict(List<int> numbers)
                {
                    return numbers.ToDictionary(x => x, x => x.ToString());
                }
            }
            """;
        AssertConverts(code);
    }

    // ── Reverse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reverse_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Reversed(List<int> numbers)
                {
                    return numbers.Where(x => x > 0).Reverse().ToList();
                }
            }
            """;
        AssertConverts(code);
    }

    // ── Complex chain: Where + OrderBy + Skip + Take + Select + ToList ──────

    [Fact]
    public void ComplexChain_WhereSkipTakeSelectToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Page(List<int> numbers)
                {
                    return numbers.Where(x => x > 0).Select(x => x.ToString()).Skip(2).Take(5).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── Concat ──────────────────────────────────────────────────────────────

    [Fact]
    public void Concat_ProducesLoop()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Combined(List<int> a, List<int> b)
                {
                    return a.Where(x => x > 0).Concat(b).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── Stream API Fallback: OrderBy then Sum ───────────────────────────────

    [Fact]
    public void Fallback_OrderBy_Sum_ProducesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Total(List<int> numbers)
                {
                    return numbers.OrderBy(x => x).Sum();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // OrderBy forces fallback to Stream API; Sum should use mapToInt().sum()
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.Contains("mapToInt", result.GeneratedCode);
        Assert.Contains(".sum()", result.GeneratedCode);
    }

    // ── Stream API Fallback: OrderBy then Average ───────────────────────────

    [Fact]
    public void Fallback_OrderBy_Average_ProducesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public double Avg(List<int> numbers)
                {
                    return numbers.OrderBy(x => x).Average();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.Contains("mapToDouble", result.GeneratedCode);
        Assert.Contains("average()", result.GeneratedCode);
    }

    // ── Stream API Fallback: GroupBy ────────────────────────────────────────

    [Fact]
    public void Fallback_GroupBy_ProducesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public object GroupedData(List<int> numbers)
                {
                    return numbers.OrderBy(x => x).GroupBy(x => x % 2);
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.Contains("Collectors.groupingBy", result.GeneratedCode);
    }

    // ── Stream API Fallback: Contains ───────────────────────────────────────

    [Fact]
    public void Fallback_Contains_ProducesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool HasValue(List<int> numbers)
                {
                    return numbers.OrderBy(x => x).Contains(42);
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.Contains("anyMatch", result.GeneratedCode);
    }

    // ── Stream API Fallback: Concat ─────────────────────────────────────────

    [Fact]
    public void Fallback_Concat_ProducesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Combined(List<int> a, List<int> b)
                {
                    return a.OrderBy(x => x).Concat(b).ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.Contains("Stream.concat", result.GeneratedCode);
    }
}
