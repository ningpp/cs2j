using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for correct Java stream source generation when C# collections map to
/// java.lang.Iterable (no .stream()) or java.util.Map (entrySet().stream()).
///
/// Root causes addressed:
///   Bug 1 - QueryExpressionTransformer: hardcoded .stream() on Iterable sources
///   Bug 2 - Concat/Union/UnionBy: argument already a stream (LINQ chain) → double .stream()
///   Bug 3 - CanCallCollectionStream: Dictionary implementsICollection → incorrect .stream()
/// </summary>
public class IterableStreamConversionTests
{
    private static string ConvertAndGetCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Bug 1 · QueryExpressionTransformer — IEnumerable source ───────────

    [Fact]
    public void QuerySyntax_IEnumerable_Source_UsesStreamSupport()
    {
        // IEnumerable<T> maps to java.lang.Iterable<T> which has no .stream();
        // QueryExpressionTransformer must use StreamSupport.stream(...spliterator(), false).
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Filter(IEnumerable<int> numbers)
                {
                    var result = from n in numbers
                                 where n > 5
                                 select n;
                    return result.ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Must not call .stream() directly on an Iterable
        Assert.DoesNotContain("numbers.stream()", java);
        // Must use StreamSupport or collect via another path
        Assert.True(
            java.Contains("StreamSupport") || java.Contains("spliterator") || java.Contains("for ("),
            $"Expected StreamSupport/spliterator/loop for IEnumerable source, got:\n{java}");
    }

    [Fact]
    public void QuerySyntax_List_Source_UsesDirectStream()
    {
        // List<T> implements Collection → .stream() is valid; no StreamSupport needed.
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
        var java = ConvertAndGetCode(code);
        Assert.True(
            java.Contains("numbers.stream()") || java.Contains("for (int"),
            $"Expected .stream() or loop for List source, got:\n{java}");
    }

    // ── Bug 2 · Concat — LINQ chain argument must not get double .stream() ─

    [Fact]
    public void Concat_WithLinqChainArgument_DoesNotDoubleWrapStream()
    {
        // c.Nodes.Concat(c.Clusters.Cast<Node>()) — the argument is a LINQ method chain
        // that has already been converted to a Stream; appending .stream() again is wrong.
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class Node { }
            class Cluster : Node { }
            class C
            {
                public List<Node> AllNodes(IEnumerable<Node> nodes, IEnumerable<Cluster> clusters)
                {
                    return nodes.Concat(clusters.Cast<Node>()).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // .map(...).stream() pattern indicates a double-wrap bug
        Assert.DoesNotContain(".stream().stream()", java);
        Assert.Contains("Stream.concat", java);
    }

    [Fact]
    public void Concat_WithIterableArgument_UsesStreamSupport()
    {
        // When the argument to Concat is a plain IEnumerable (not a LINQ chain),
        // it must be wrapped via StreamSupport rather than .stream().
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Combine(List<int> a, IEnumerable<int> b)
                {
                    return a.Concat(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat", java);
        // b is IEnumerable, must not call b.stream() directly
        Assert.DoesNotContain("b.stream()", java);
    }

    [Fact]
    public void Concat_BothLists_UseDirectStream()
    {
        // Both sides are List → .stream() is valid throughout.
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Combine(List<int> a, List<int> b)
                {
                    return a.Concat(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat", java);
    }

    // ── Bug 2 · Union — same Iterable/LINQ-chain wrapping fix ─────────────

    [Fact]
    public void Union_WithListArgument_Converts()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Merge(List<int> a, List<int> b)
                {
                    return a.Union(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat", java);
        Assert.Contains("distinct()", java);
    }

    [Fact]
    public void Union_WithIterableArgument_UsesStreamSupport()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<int> Merge(List<int> a, IEnumerable<int> b)
                {
                    return a.Union(b).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Stream.concat", java);
        Assert.Contains("distinct()", java);
        Assert.DoesNotContain("b.stream()", java);
    }

    // ── Bug 3 · Dictionary .Select() must use entrySet().stream() ─────────

    [Fact]
    public void Dictionary_Select_UsesEntrySetStream()
    {
        // Dictionary<K,V> implements ICollection<KVP>, but maps to Java HashMap which
        // has no .stream(). Correct translation is entrySet().stream().
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> GetKeys(Dictionary<string, int> dict)
                {
                    return dict.Select(kvp => kvp.Key).ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Must not use dict.stream() which doesn't exist on HashMap
        Assert.DoesNotContain("dict.stream()", java);
        // Correct pattern is entrySet().stream() or procedural loop
        Assert.True(
            java.Contains("entrySet()") || java.Contains("for (") || java.Contains("StreamSupport"),
            $"Expected entrySet()/loop for Dictionary source, got:\n{java}");
    }

    // ── Query syntax join clause ───────────────────────────────────────────

    [Fact]
    public void QuerySyntax_Join_IEnumerable_UsesStreamSupport()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                public List<string> JoinTest(IEnumerable<int> ids, IEnumerable<string> names)
                {
                    var result = from id in ids
                                 join name in names on id.ToString() equals name
                                 select name;
                    return result.ToList();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should not produce .stream() on Iterable
        Assert.DoesNotContain("ids.stream()", java);
        Assert.DoesNotContain("names.stream()", java);
    }
}
