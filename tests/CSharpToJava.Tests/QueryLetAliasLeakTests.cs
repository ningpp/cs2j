using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for QueryLetAliases leaking across methods.
/// LINQ 'let' clause aliases must be scoped to their owning query expression
/// and must not shadow parameter names in subsequent methods.
/// </summary>
public class QueryLetAliasLeakTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    [Fact]
    public void LinqLetAlias_DoesNotLeakIntoNextMethod()
    {
        // Method1 has a LINQ query with 'let b = ...'
        // Method2 has a parameter named 'b'
        // The parameter 'b' in Method2 must NOT be replaced by the let expression.
        const string code = """
            using System.Linq;
            using System.Collections.Generic;

            public class TestClass {
                private List<int> items = new List<int>();

                public IEnumerable<int> Method1() {
                    return from x in items
                           let b = x * 2
                           where b > 10
                           select b;
                }

                public int Method2(int a, int b) {
                    return a + b;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        // Method2 must use the parameter names directly, not LINQ let expressions
        Assert.Contains("a + b", result.GeneratedCode);
    }

    [Fact]
    public void LinqLetAlias_DoesNotLeakWithinSameMethod()
    {
        // A LINQ query with 'let a = ...' followed by code using a local variable 'a'
        // in the same method. The let alias must not affect the later usage.
        const string code = """
            using System.Linq;
            using System.Collections.Generic;

            public class TestClass {
                private List<int> items = new List<int>();

                public int Method1() {
                    var result = (from x in items
                                  let a = x * 3
                                  select a).ToList();

                    int a = result.Count;
                    return a;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        // The local variable 'a' after the LINQ query must not be replaced by the let expression
        Assert.Contains("int a = result", result.GeneratedCode);
        Assert.Contains("return a;", result.GeneratedCode);
    }
}
