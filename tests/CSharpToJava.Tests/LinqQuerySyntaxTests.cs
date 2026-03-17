using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for LINQ query syntax (from/where/select/let/orderby/join/group)
/// conversion via LinqRewriter query desugaring.
/// </summary>
public class LinqQuerySyntaxTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Basic from/where/select ─────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_FromWhereSelect_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Filter(List<int> numbers)
                {
                    var result = from n in numbers
                                 where n > 5
                                 select n;
                    return result.ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── from/where/select with projection ───────────────────────────────────

    [Fact]
    public void QuerySyntax_SelectProjection_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> Transform(List<int> numbers)
                {
                    var result = from n in numbers
                                 where n > 5
                                 select n.ToString();
                    return result.ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── let clause ──────────────────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_LetClause_InlinesExpression()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> WithLet(List<int> numbers)
                {
                    var result = from n in numbers
                                 let doubled = n * 2
                                 where doubled > 10
                                 select doubled.ToString();
                    return result.ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── orderby ─────────────────────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_OrderBy_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Sorted(List<int> numbers)
                {
                    var result = from n in numbers
                                 orderby n descending
                                 select n;
                    return result.ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── group by ────────────────────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_GroupBy_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public object Grouped(List<int> numbers)
                {
                    var result = from n in numbers
                                 group n by n % 2;
                    return result;
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }

    // ── into continuation ───────────────────────────────────────────────────

    [Fact]
    public void QuerySyntax_IntoContinuation_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> WithContinuation(List<int> numbers)
                {
                    var result = from n in numbers
                                 where n > 5
                                 select n * 2
                                 into doubled
                                 where doubled < 100
                                 select doubled;
                    return result.ToList();
                }
            }
            """;
        var result = Convert(code);
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
    }
}
