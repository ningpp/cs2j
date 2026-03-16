using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for IEnumerable.Any() conversion.
///
/// Bug fixed:
///   colls.Any() (no predicate) was incorrectly emitted as colls.anyMatch(),
///   which is invalid Java — anyMatch(Predicate) is a Stream terminal op and does
///   not exist on Iterable&lt;T&gt;. The correct translation is colls.iterator().hasNext().
/// </summary>
public class EnumerableAnyTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Bug fix: Any() with no arguments ───────────────────────────────────────

    [Fact]
    public void Any_NoArgs_EmitsIteratorHasNext()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool IEnumerableAny(IEnumerable<int> colls)
                {
                    return colls != null && colls.Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("iterator().hasNext()", result.GeneratedCode);
        Assert.DoesNotContain("anyMatch()", result.GeneratedCode);
    }

    [Fact]
    public void Any_NoArgs_WithNullCheck_PreservesShortCircuit()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public bool Check(IEnumerable<int> colls)
                {
                    return colls != null && colls.Any();
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Both the null guard and the iterator check must be present.
        Assert.Contains("colls != null", result.GeneratedCode);
        Assert.Contains("iterator().hasNext()", result.GeneratedCode);
    }
}
