using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Phase 10 tests: Structured observability — skip reasons, rewrite statistics, operator tracking.
/// </summary>
public class LinqObservabilityPhase10Tests
{
    [Fact]
    public void Statistics_PopulatedOnSuccessfulRewrite()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return items.Where(x => x > 0).Select(x => x * 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        Assert.True(result.LinqStatistics.RewrittenChainCount > 0, "Should have rewritten at least one chain");
        Assert.True(result.LinqStatistics.RewrittenMethodCount > 0, "Should have rewritten at least one method");
    }

    [Fact]
    public void Statistics_DesugaredQueryCountPopulatedForQuerySyntax()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    List<int> M(List<int> items) {
        return (from x in items where x > 0 select x * 2).ToList();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        Assert.True(result.LinqStatistics.DesugaredQueryCount > 0, "Should have desugared at least one query");
    }

    [Fact]
    public void Statistics_EncounteredOperatorsTracked()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).Sum();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        // Both Where and Sum should be tracked as encountered
        Assert.True(result.LinqStatistics.EncounteredOperators.Count >= 2,
            $"Expected >= 2 operators, got {result.LinqStatistics.EncounteredOperators.Count}");
        Assert.True(result.LinqStatistics.EncounteredOperators.Any(o => o.MethodFullName.Contains("Where")),
            "Where should be tracked");
        Assert.True(result.LinqStatistics.EncounteredOperators.Any(o => o.MethodFullName.Contains("Sum")),
            "Sum should be tracked");
    }

    [Fact]
    public void Statistics_RewrittenOperatorsMarked()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).Sum();
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        // On successful rewrite, operators should be marked as rewritten
        Assert.True(result.LinqStatistics.EncounteredOperators.Any(o => o.WasRewritten),
            "At least one operator should be marked as rewritten");
    }

    [Fact]
    public void Statistics_NullWhenLinqRewriteDisabled()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = @"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        return items.Where(x => x > 0).Sum();
    }
}",
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
                EnableLinqRewrite = false,
            },
        });
        Assert.True(result.Success, result.GeneratedCode);
        // When LINQ rewrite is disabled, no statistics should be collected
        Assert.Null(result.LinqStatistics);
    }

    [Fact]
    public void GetUncoveredOperatorsByFrequency_ReturnsCorrectResults()
    {
        var stats = new LinqRewriteStatistics();
        // Add some operators, some rewritten, some not
        stats.EncounteredOperators.Add(new LinqOperatorOccurrence("System.Linq.Enumerable.Foo()", 1, false));
        stats.EncounteredOperators.Add(new LinqOperatorOccurrence("System.Linq.Enumerable.Foo()", 5, false));
        stats.EncounteredOperators.Add(new LinqOperatorOccurrence("System.Linq.Enumerable.Bar()", 10, false));
        stats.EncounteredOperators.Add(new LinqOperatorOccurrence("System.Linq.Enumerable.Baz()", 15, true));

        var uncovered = stats.GetUncoveredOperatorsByFrequency();

        // Baz was rewritten so it's not uncovered
        Assert.Equal(2, uncovered.Count);
        // Foo has 2 occurrences, Bar has 1
        Assert.Equal("System.Linq.Enumerable.Foo()", uncovered[0].Key);
        Assert.Equal(2, uncovered[0].Value);
        Assert.Equal("System.Linq.Enumerable.Bar()", uncovered[1].Key);
        Assert.Equal(1, uncovered[1].Value);
    }

    [Fact]
    public void MergeFrom_AggregatesCorrectly()
    {
        var stats1 = new LinqRewriteStatistics { DesugaredQueryCount = 2, RewrittenChainCount = 3, RewrittenMethodCount = 1 };
        stats1.SkippedChains.Add(new LinqSkipInfo(LinqSkipReason.RuleExpansionFailed, 10, "Sum", "test"));
        stats1.EncounteredOperators.Add(new LinqOperatorOccurrence("Op1", 1, true));

        var stats2 = new LinqRewriteStatistics { DesugaredQueryCount = 1, RewrittenChainCount = 2, RewrittenMethodCount = 1 };
        stats2.EncounteredOperators.Add(new LinqOperatorOccurrence("Op2", 5, false));

        stats1.MergeFrom(stats2);

        Assert.Equal(3, stats1.DesugaredQueryCount);
        Assert.Equal(5, stats1.RewrittenChainCount);
        Assert.Equal(2, stats1.RewrittenMethodCount);
        Assert.Single(stats1.SkippedChains);
        Assert.Equal(2, stats1.EncounteredOperators.Count);
    }

    [Fact]
    public void LinqSkipInfo_RecordsStructuredData()
    {
        var info = new LinqSkipInfo(LinqSkipReason.UnsupportedMethodChain, 42, "Custom", "Not supported");
        Assert.Equal(LinqSkipReason.UnsupportedMethodChain, info.Reason);
        Assert.Equal(42, info.LineNumber);
        Assert.Equal("Custom", info.MethodName);
        Assert.Equal("Not supported", info.Message);
    }

    [Fact]
    public void Statistics_MultipleChains_TracksAll()
    {
        var result = ConvertProcedural(@"
using System.Collections.Generic;
using System.Linq;
class C {
    int M(List<int> items) {
        var a = items.Where(x => x > 0).Sum();
        var b = items.Select(x => x * 2).Count();
        return a + b;
    }
}");
        Assert.True(result.Success, result.GeneratedCode);
        Assert.NotNull(result.LinqStatistics);
        Assert.True(result.LinqStatistics.RewrittenChainCount >= 2,
            $"Expected >= 2 rewritten chains, got {result.LinqStatistics.RewrittenChainCount}");
    }

    // ─── Helpers ───

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                PreferStreamApi = false,
            },
        });
    }
}
