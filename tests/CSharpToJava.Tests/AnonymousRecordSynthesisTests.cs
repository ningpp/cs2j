using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for anonymous type → Java record synthesis and related LINQ improvements.
/// </summary>
public class AnonymousRecordSynthesisTests
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

    // ── Anonymous type → record synthesis (Java 17+) ─────────────────────

    [Fact]
    public void AnonymousType_SynthesizesRecord_Java21()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public void Test(List<string> names)
                {
                    var items = names.Select(n => new { Name = n, Length = n.Length }).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should synthesize a record declaration
        Assert.Contains("record", java);
        // Should use new RecordName(...) instead of Map.of(...)
        Assert.DoesNotContain("Map.of(", java);
    }

    // ── Anonymous type falls back to Map for Java 8 ─────────────────────

    [Fact]
    public void AnonymousType_FallsBackToMap_Java8()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public void Test(List<string> names)
                {
                    var items = names.Select(n => new { Name = n, Length = n.Length }).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code, new ConversionOptions
        {
            TargetJavaVersion = JavaVersion.Java8,
            UseRecords = false,
        });
        // Should use Map.of() for Java 8
        Assert.Contains("Map.of(", java);
        Assert.DoesNotContain("record ", java);
    }

    // ── Where + OrderByDescending + Select(anonymous) + ToList ──────────

    [Fact]
    public void FullChain_Where_OrderByDesc_SelectAnonymous_ToList()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class Employee
            {
                public string FirstName { get; set; }
                public string LastName { get; set; }
                public int Salary { get; set; }
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

        // Should contain a synthesized record
        Assert.Contains("record", java);
        // Should NOT use Map.of
        Assert.DoesNotContain("Map.of(", java);
        // Should use stream operations
        Assert.Contains(".filter(", java);
        // Should have a sorted with reversed comparator
        Assert.Contains(".reversed()", java);
    }

    // ── Record deduplication ────────────────────────────────────────────

    [Fact]
    public void AnonymousType_Deduplicates_IdenticalStructure()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public void Test(List<string> names)
                {
                    var items1 = names.Select(n => new { Name = n, Len = n.Length }).ToList();
                    var items2 = names.Select(n => new { Name = n, Len = n.Length }).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Count occurrences of "record " — should be exactly one (deduplicated)
        var recordCount = java.Split(new[] { "record " }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, recordCount);
    }

    // ── .toList() shorthand for Java 21+ ────────────────────────────────

    [Fact]
    public void ToList_UsesToListShorthand_Java21()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code, new ConversionOptions
        {
            TargetJavaVersion = JavaVersion.Java21,
        });
        Assert.Contains(".toList()", java);
        Assert.DoesNotContain("Collectors.toList()", java);
    }

    [Fact]
    public void ToList_UsesCollectors_Java8()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Test(List<int> nums)
                {
                    return nums.OrderBy(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code, new ConversionOptions
        {
            TargetJavaVersion = JavaVersion.Java8,
        });
        Assert.Contains("Collectors.toList()", java);
        // Should NOT use the shorthand .toList() directly on stream (only Collectors form)
        Assert.DoesNotContain(").toList()", java);
    }

    // ── OrderBy + ThenBy merging ────────────────────────────────────────

    [Fact]
    public void OrderBy_ThenBy_MergesComparators()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Test(List<string> items)
                {
                    return items.OrderBy(x => x.Length).ThenBy(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should merge into one sorted() with thenComparing
        Assert.Contains(".thenComparing(", java);
        // Should not have two separate .sorted() calls
        var sortedCount = java.Split(new[] { ".sorted(" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, sortedCount);
    }

    [Fact]
    public void OrderBy_ThenByDescending_MergesComparators()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Test(List<string> items)
                {
                    return items.OrderBy(x => x.Length).ThenByDescending(x => x).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains(".thenComparing(", java);
        Assert.Contains(".reversed()", java);
    }
}
