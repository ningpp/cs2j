using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for LINQ Stream API fallback conversion.
/// These test the InvocationExpressionTransformer handlers that map
/// LINQ methods to Java Stream API when LinqRewriter cannot rewrite them.
/// </summary>
public class LinqStreamApiFallbackTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    private static string ConvertAndGetCode(string code)
    {
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── SelectMany (flatMap) ────────────────────────────────────────────────

    [Fact]
    public void SelectMany_ProducesFlatMap()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Flatten(List<List<int>> lists)
                {
                    return lists.SelectMany(l => l).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("flatMap", java);
        Assert.Contains("collect", java);
    }

    // ── Where → filter ──────────────────────────────────────────────────────

    [Fact]
    public void Where_OrderBy_Select_ProducesFilterSortedMap()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Test(List<int> nums)
                {
                    return nums.Where(x => x > 0).OrderBy(x => x).Select(x => x.ToString()).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".filter(", java);
        Assert.Contains(".sorted(", java);
        Assert.Contains(".map(", java);
        Assert.Contains(".collect(", java);
    }

    // ── Distinct ────────────────────────────────────────────────────────────

    [Fact]
    public void Distinct_OrderBy_ProducesDistinct()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Distinct().ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".distinct()", java);
    }

    // ── Skip → skip ─────────────────────────────────────────────────────────

    [Fact]
    public void Skip_OrderBy_ProducesSkip()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Skip(5).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".skip(5)", java);
    }

    // ── Take → limit ────────────────────────────────────────────────────────

    [Fact]
    public void Take_OrderBy_ProducesLimit()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Take(10).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".limit(10)", java);
    }

    // ── First → findFirst().orElseThrow() ───────────────────────────────────

    [Fact]
    public void First_OrderBy_ProducesFindFirst()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).First();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("findFirst()", java);
        Assert.Contains("orElseThrow()", java);
    }

    // ── FirstOrDefault → findFirst().orElse(null) ───────────────────────────

    [Fact]
    public void FirstOrDefault_OrderBy_ProducesFindFirstOrElse()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).FirstOrDefault();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("findFirst()", java);
        Assert.Contains("orElse(null)", java);
    }

    // ── Last → reduce ───────────────────────────────────────────────────────

    [Fact]
    public void Last_OrderBy_ProducesReduce()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Last();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("reduce(", java);
        Assert.Contains("orElseThrow()", java);
    }

    // ── Any(predicate) → anyMatch ───────────────────────────────────────────

    [Fact]
    public void Any_WithPredicate_OrderBy_ProducesAnyMatch()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Any(x => x > 5);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("anyMatch(", java);
    }

    // ── All(predicate) → allMatch ───────────────────────────────────────────

    [Fact]
    public void All_OrderBy_ProducesAllMatch()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).All(x => x > 0);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("allMatch(", java);
    }

    // ── Count() ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_OrderBy_ProducesCount()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Count();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".count()", java);
    }

    // ── Count(predicate) ────────────────────────────────────────────────────

    [Fact]
    public void Count_WithPredicate_OrderBy_ProducesFilterCount()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Count(x => x > 0);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".filter(", java);
        Assert.Contains(".count()", java);
    }

    // ── Min/Max ─────────────────────────────────────────────────────────────

    [Fact]
    public void Min_OrderBy_ProducesMin()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Min();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".min(", java);
    }

    [Fact]
    public void Max_OrderBy_ProducesMax()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).Max();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".max(", java);
    }

    // ── ToArray ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToArray_OrderBy_ProducesToArray()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public object Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).ToArray();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".toArray()", java);
    }

    // ── Complex chain: SelectMany + Where + OrderBy + Take + ToList ─────────

    [Fact]
    public void ComplexChain_SelectManyWhereOrderByTakeToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<List<int>> lists)
                {
                    return lists.SelectMany(l => l)
                                .Where(x => x > 0)
                                .OrderBy(x => x)
                                .Take(10)
                                .ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("flatMap(", java);
        Assert.Contains("filter(", java);
        Assert.Contains("sorted(", java);
        Assert.Contains("limit(10)", java);
        Assert.Contains("collect(", java);
    }

    // ── OrderByDescending ───────────────────────────────────────────────────

    [Fact]
    public void OrderByDescending_ProducesSortedReversed()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderByDescending(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("sorted(", java);
        Assert.Contains("reversed()", java);
        Assert.Contains("collect(", java);
    }

    // ── ToDictionary with key+value ─────────────────────────────────────────

    [Fact]
    public void ToDictionary_OrderBy_ProducesToMap()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public Dictionary<int, string> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).ToDictionary(x => x, x => x.ToString());
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Collectors.toMap(", java);
    }

    [Fact]
    public void IEnumerableReceiver_OrderBy_Select_ToList_UsesStreamSupport()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Test(IEnumerable<int> nums)
                {
                    return nums.OrderBy(x => x).Select(x => x.ToString()).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("StreamSupport.stream(", java);
        Assert.Contains("spliterator()", java);
    }

    [Fact]
    public void ICollectionReceiver_OrderBy_Select_ToList_UsesCollectionStream()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Test(ICollection<int> nums)
                {
                    return nums.OrderBy(x => x).Select(x => x.ToString()).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("nums.stream()", java);
        Assert.DoesNotContain("StreamSupport.stream(nums", java);
    }

    [Fact]
    public void IEnumerableReceiver_OrderBy_ToDictionary_UsesStreamSupport()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public Dictionary<int, string> Test(IEnumerable<int> nums)
                {
                    return nums.OrderBy(x => x).ToDictionary(x => x, x => x.ToString());
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("StreamSupport.stream(", java);
        Assert.Contains("Collectors.toMap(", java);
    }

    // ── GroupBy ─────────────────────────────────────────────────────────────

    [Fact]
    public void GroupBy_OrderBy_ProducesGroupingBy()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public object Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).GroupBy(x => x % 2);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Collectors.groupingBy(", java);
    }

    // ── First with predicate ────────────────────────────────────────────────

    [Fact]
    public void First_WithPredicate_ProducesFilterFindFirst()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).First(x => x > 5);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".filter(", java);
        Assert.Contains("findFirst()", java);
        Assert.Contains("orElseThrow()", java);
    }
}
