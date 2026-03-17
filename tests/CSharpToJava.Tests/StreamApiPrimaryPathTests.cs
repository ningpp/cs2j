using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for the Stream API primary path — when PreferStreamApi is true (default for Java 9+),
/// LINQ method chains should be converted to Java Stream API calls instead of procedural loops.
/// </summary>
public class StreamApiPrimaryPathTests
{
    private static ConversionResult Convert(string csharpCode, ConversionOptions? options = null)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = csharpCode,
            Options = options ?? new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java21,
                UseRecords = true,
                PreferStreamApi = true,
            }
        });
    }

    private static string ConvertAndGetCode(string code, ConversionOptions? options = null)
    {
        var result = Convert(code, options);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── User's exact example: Where + OrderByDescending + Select(anonymous) + ToList ──

    [Fact]
    public void UserExample_Where_OrderByDesc_SelectAnonymous_ToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class Employee
            {
                public string FirstName { get; set; }
                public string LastName { get; set; }
                public decimal Salary { get; set; }
            }
            class C
            {
                public void Test(List<Employee> employees)
                {
                    var highEarners = employees
                        .Where(e => e.Salary > 100000)
                        .OrderByDescending(e => e.LastName)
                        .Select(e => new { e.FirstName, e.LastName, FullName = e.FirstName + " " + e.LastName })
                        .ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should produce Stream API, not procedural loop
        Assert.Contains(".stream()", java);
        Assert.Contains(".filter(", java);
        Assert.Contains(".sorted(", java);
        Assert.Contains(".reversed()", java);
        Assert.Contains(".map(", java);
        Assert.Contains(".toList()", java);
        // Should NOT contain procedural loop markers
        Assert.DoesNotContain("_ProceduralLinq", java);
    }

    // ── Stream API produces .stream() chains ────────────────────────────────

    [Fact]
    public void PreferStreamApi_Where_Select_ToList_ProducesStreamApi()
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
        var java = ConvertAndGetCode(code);
        Assert.Contains(".stream()", java);
        Assert.Contains(".filter(", java);
        Assert.Contains(".map(", java);
        Assert.Contains(".toList()", java);
        Assert.DoesNotContain("_ProceduralLinq", java);
    }

    // ── Static generators ───────────────────────────────────────────────────

    [Fact]
    public void EnumerableRange_ProducesIntStreamRange()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> GetRange()
                {
                    return Enumerable.Range(1, 10).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("IntStream.range(", java);
        Assert.Contains(".boxed()", java);
    }

    [Fact]
    public void EnumerableRepeat_ProducesStreamGenerate()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> GetRepeated()
                {
                    return Enumerable.Repeat("hello", 5).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.generate(", java);
        Assert.Contains(".limit(", java);
    }

    // ── Append / Prepend ────────────────────────────────────────────────────

    [Fact]
    public void Append_ProducesStreamConcat()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.Append(42).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat(", java);
        Assert.Contains("Stream.of(42)", java);
    }

    [Fact]
    public void Prepend_ProducesStreamConcat()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.Prepend(0).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat(", java);
        Assert.Contains("Stream.of(0)", java);
    }

    // ── Reverse ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reverse_ProducesCollectAndReverse()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.OrderBy(x => x).Reverse().ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Collections.reverse", java);
        Assert.DoesNotContain("/* reverse */", java);
    }

    // ── SelectMany two-arg overload ─────────────────────────────────────────

    [Fact]
    public void SelectMany_SingleArg_ProducesFlatMap()
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
        Assert.Contains(".flatMap(", java);
    }

    // ── Intersect / Except with HashSet ─────────────────────────────────────

    [Fact]
    public void Intersect_ProducesHashSetContains()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> a, List<int> b)
                {
                    return a.OrderBy(x => x).Intersect(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("HashSet", java);
        Assert.Contains("contains", java);
    }

    [Fact]
    public void Except_ProducesHashSetContains()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> a, List<int> b)
                {
                    return a.OrderBy(x => x).Except(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("HashSet", java);
        Assert.Contains("!new HashSet", java);
    }

    // ── OrderBy + ThenBy merging ────────────────────────────────────────────

    [Fact]
    public void OrderBy_ThenBy_ThenByDescending_MergesComparators()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class Item
            {
                public string Category { get; set; }
                public string Name { get; set; }
                public int Price { get; set; }
            }
            class C
            {
                public List<Item> Test(List<Item> items)
                {
                    return items.OrderBy(x => x.Category).ThenBy(x => x.Name).ThenByDescending(x => x.Price).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should have a single .sorted() call with chained .thenComparing() calls
        Assert.Contains(".thenComparing(", java);
    }

    // ── Java version variations ─────────────────────────────────────────────

    [Fact]
    public void Java8_UsesCollectorsToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.OrderBy(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code, new ConversionOptions
        {
            TargetJavaVersion = JavaVersion.Java8,
            PreferStreamApi = true,
        });
        Assert.Contains("Collectors.toList()", java);
        Assert.DoesNotContain(".toList()", java.Replace("Collectors.toList()", ""));
    }

    [Fact]
    public void Java21_UsesToListShorthand()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.OrderBy(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".toList()", java);
        Assert.DoesNotContain("Collectors.toList()", java);
    }

    // ── Default behavior: Java 17+ uses Stream API by default ───────────────

    [Fact]
    public void DefaultOptions_Java17_UsesStreamApi()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.Where(x => x > 0).Select(x => x * 2).ToList();
                }
            }
            """;
        // Use default options (Java 17, no explicit PreferStreamApi)
        var result = Convert(code, new ConversionOptions { TargetJavaVersion = JavaVersion.Java17 });
        Assert.True(result.Success);
        Assert.Contains(".stream()", result.GeneratedCode);
        Assert.DoesNotContain("_ProceduralLinq", result.GeneratedCode);
    }

    [Fact]
    public void DefaultOptions_Java8_UsesProcedural()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> items)
                {
                    return items.Where(x => x > 0).Select(x => x * 2).ToList();
                }
            }
            """;
        // Java 8 default → procedural path
        var result = Convert(code, new ConversionOptions { TargetJavaVersion = JavaVersion.Java8 });
        Assert.True(result.Success);
        Assert.DoesNotContain(".stream()", result.GeneratedCode);
    }

    // ── Backward compatibility: --prefer-procedural works ───────────────────

    [Fact]
    public void PreferProcedural_ForcesLinqRewriter()
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
        var java = ConvertAndGetCode(code, new ConversionOptions
        {
            TargetJavaVersion = JavaVersion.Java21,
            PreferStreamApi = false,
        });
        // With PreferStreamApi=false, should use procedural LinqRewriter
        Assert.DoesNotContain(".stream()", java);
        Assert.DoesNotContain(".filter(", java);
    }

    // ── Sum type detection ──────────────────────────────────────────────────

    [Fact]
    public void Sum_ProducesMapToIntSum()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public int Test(List<int> items)
                {
                    return items.OrderBy(x => x).Sum();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("mapToInt", java);
        Assert.Contains(".sum()", java);
    }

    // ── Anonymous type record synthesis with Stream API path ─────────────────

    [Fact]
    public void AnonymousType_WithStreamApi_SynthesizesRecord()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public void Test(List<string> names)
                {
                    var items = names.OrderBy(n => n).Select(n => new { Name = n, Length = n.Length }).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("record", java);
        Assert.Contains(".stream()", java);
    }

    // ── Record naming heuristics ─────────────────────────────────────────────

    [Fact]
    public void RecordNaming_Categories_ProducesSingularCategory()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public void Test(List<string> data)
                {
                    var categories = data.OrderBy(x => x).Select(x => new { Value = x }).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // "categories" should singularize to "Category" (ies→y rule)
        Assert.Contains("Category", java);
    }
}
