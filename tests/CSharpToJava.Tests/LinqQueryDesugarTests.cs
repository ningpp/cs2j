using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for issue #11: LINQ query expression desugaring.
///
/// LINQ query syntax (from…in…where…select) is desugared to method-call chains
/// (Where/Select/OrderBy/GroupBy) by LinqQueryDesugarer before the LinqRewriter
/// converts them to procedural loops (when PreferStreamApi=false).
///
/// This ensures that query expressions and method-call chains produce identical
/// output, including proper procedural loops when stream API is disabled.
/// </summary>
public class LinqQueryDesugarTests
{
    // ─────────────────────────────────────────────────────────────────────
    // Procedural mode: query syntax should match method-syntax output
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_Where_Any_ProducesProceduralCode_WhenStreamApiDisabled()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasPositive(List<int> values) {
        return (from v in values where v > 0 select v).Any();
    }
}";
        var result = ConvertProcedural(source);

        Assert.True(result.Success, result.GeneratedCode);
        // Procedural loop, not stream API
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("for (", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QuerySyntax_WhereAny_MatchesMethodChain_WhenStreamApiDisabled()
    {
        // Both forms should produce identical procedural Java code.
        const string querySource = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasPositive(List<int> values) {
        return (from v in values where v > 0 select v).Any();
    }
}";
        const string methodSource = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasPositive(List<int> values) {
        return values.Where(v => v > 0).Any();
    }
}";
        var queryResult = ConvertProcedural(querySource);
        var methodResult = ConvertProcedural(methodSource);

        Assert.True(queryResult.Success, queryResult.GeneratedCode);
        Assert.True(methodResult.Success, methodResult.GeneratedCode);
        Assert.Equal(queryResult.GeneratedCode, methodResult.GeneratedCode);
    }

    [Fact]
    public void QuerySyntax_WhereAny_WithPredicate_MatchesMethodChain_WhenStreamApiDisabled()
    {
        const string querySource = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasLarger(List<int> values, int threshold) {
        return (from v in values where v > threshold select v).Any();
    }
}";
        const string methodSource = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasLarger(List<int> values, int threshold) {
        return values.Where(v => v > threshold).Any();
    }
}";
        var queryResult = ConvertProcedural(querySource);
        var methodResult = ConvertProcedural(methodSource);

        Assert.True(queryResult.Success, queryResult.GeneratedCode);
        Assert.True(methodResult.Success, methodResult.GeneratedCode);
        Assert.Equal(queryResult.GeneratedCode, methodResult.GeneratedCode);
    }

    [Fact]
    public void QuerySyntax_Where_Count_ProducesProceduralCode_WhenStreamApiDisabled()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    int CountPositive(List<int> values) {
        return (from v in values where v > 0 select v).Count();
    }
}";
        var result = ConvertProcedural(source);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("for (", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QuerySyntax_Where_First_ProducesProceduralCode_WhenStreamApiDisabled()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    int FirstPositive(List<int> values) {
        return (from v in values where v > 0 select v).First();
    }
}";
        var result = ConvertProcedural(source);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("for (", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QuerySyntax_IdentitySelect_DoesNotAddSelectCall()
    {
        // "from v in values select v" is an identity — should not emit .Select(v => v)
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool Has(List<int> values) {
        return (from v in values select v).Any(v => v > 0);
    }
}";
        var result = ConvertProcedural(source);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("for (", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void QuerySyntax_GroupBy_IsSupported()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    void Group(List<string> words) {
        var g = from w in words group w by w.Length;
    }
}";
        var result = ConvertStream(source);

        Assert.True(result.Success, result.GeneratedCode);
    }

    [Fact]
    public void QuerySyntax_SimpleSelect_IsSupported()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasProjected(List<int> values) {
        return (from v in values select v * 2).Any(v => v > 0);
    }
}";
        var result = ConvertProcedural(source);

        Assert.True(result.Success, result.GeneratedCode);
        Assert.DoesNotContain(".stream()", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPipeline_QuerySyntax_ProducesProceduralCode_WhenStreamApiDisabled()
    {
        var results = await new ProjectConversionPipeline(ProceduralOptions()).ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool HasPositive(List<int> values) {
        return (from v in values where v > 0 select v).Any();
    }
}",
            }
        });

        var primaryResult = Assert.Single(results, r => r.FileName == "Sample.java");

        Assert.True(primaryResult.Success, primaryResult.GeneratedCode);
        Assert.DoesNotContain(".stream()", primaryResult.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("for (", primaryResult.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectLinqDesugarPass_CountsDesugaredQueriesAndRewrittenChains()
    {
        var options = ProceduralOptions();
        var pipeline = new ProjectConversionPipeline(options);
        await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool A(List<int> v) { return (from x in v where x > 0 select x).Any(); }
    bool B(List<int> v) { return (from x in v where x > 1 select x).Any(); }
}",
            }
        });

        var linqMetric = Assert.Single(pipeline.LastPassMetrics, m => m.Name == "ProjectLinqDesugarPass");
        // 2 query expressions desugared (Phase 1) + 2 .Any() chains converted to loops (Phase 2) = 4
        Assert.Equal(4, linqMetric.RewriteCount);
    }

    [Fact]
    public void QuerySyntax_ComplexCase_Let_FallsBackToQueryExpressionTransformer()
    {
        // 'let' clauses require transparent identifiers (anonymous types) and are not
        // desugared; they fall back to QueryExpressionTransformer which uses Stream API.
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class Sample {
    bool Any(List<int> values) {
        return (from v in values let doubled = v * 2 where doubled > 5 select doubled).Any();
    }
}";
        // In default stream mode this should succeed
        var result = ConvertStream(source);
        Assert.True(result.Success, result.GeneratedCode);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static ConversionResult ConvertProcedural(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = ProceduralOptions(),
        });
    }

    private static ConversionResult ConvertStream(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
                EmitCompatibilityHelpers = false,
            },
        });
    }

    private static ConversionOptions ProceduralOptions() => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        EmitCompatibilityHelpers = false,
        PreferStreamApi = false,
    };
}
