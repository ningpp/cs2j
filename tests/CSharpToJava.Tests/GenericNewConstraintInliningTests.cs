using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class GenericNewConstraintInliningTests
{
    [Fact]
    public void AddToMap_WithCustomSetTypeParameter_InlinesCorrectly()
    {
        var result = Convert("""
using System.Collections.Generic;

public class Set<T> : ICollection<T>
{
    private readonly HashSet<T> _inner = new HashSet<T>();
    public int Count => _inner.Count;
    public bool IsReadOnly => false;
    public void Add(T item) => _inner.Add(item);
    public IEnumerator<T> GetEnumerator() => _inner.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Clear() => _inner.Clear();
    public bool Contains(T item) => _inner.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => _inner.CopyTo(array, arrayIndex);
    public bool Remove(T item) => _inner.Remove(item);
    public Set() { }
}

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}

public class EdgeGeometry
{
    public double LineWidth { get; set; }
}

public class CdtEdge
{
    public double Capacity { get; set; }
}

public class BundleRouter
{
    public Dictionary<CdtEdge, Set<EdgeGeometry>> GetPathsOnCdtEdge(
        Dictionary<EdgeGeometry, Set<CdtEdge>> crossedEdges)
    {
        var res = new Dictionary<CdtEdge, Set<EdgeGeometry>>();
        foreach (var edge in crossedEdges.Keys)
        {
            foreach (var cdtEdge in crossedEdges[edge])
                CollectionUtilities.AddToMap(res, cdtEdge, edge);
        }
        return res;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.DoesNotContain("new CSharpList<>()", code);
        Assert.Contains("new Set<EdgeGeometry>()", code);
        Assert.Contains(".get(", code);
    }

    [Fact]
    public void AddToMap_WithArrayListTypeParameter_StillWorks()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}

public class Demo
{
    public Dictionary<int, List<string>> Build()
    {
        var res = new Dictionary<int, List<string>>();
        CollectionUtilities.AddToMap(res, 1, "hello");
        return res;
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.DoesNotContain("new CSharpList<>()", code);
        Assert.Contains("new CSharpList<String>()", code);
    }

    [Fact]
    public void AddToMap_StandaloneMethod_HasFallback()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_IEnumerableConstraint_FallbackHasExplicitTypeArg()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : IEnumerable<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_ConcreteElementType_FallbackHasConcreteTypeArg()
    {
        var result = Convert("""
using System.Collections.Generic;

public static class CollectionUtilities
{
    public static void AddToMap<T, TC>(Dictionary<T, TC> dictionary, T key, string value)
        where TC : ICollection<string>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<String>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    [Fact]
    public void AddToMap_Standalone_MultipleConstraints_FallbackUsesCollectionConstraint()
    {
        var result = Convert("""
using System.Collections.Generic;

public interface ICustomSink { void Flush(); }

public static class CollectionUtilities
{
    public static void AddToMap<TS, T, TC>(Dictionary<T, TC> dictionary, T key, TS value)
        where TC : ICustomSink, ICollection<TS>, new()
    {
        TC tc;
        if (!dictionary.TryGetValue(key, out tc))
            dictionary[key] = tc = new TC();
        tc.Add(value);
    }
}
""");

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.Message)));
        var code = result.GeneratedCode!;

        Assert.Contains("(TC) new CSharpList<TS>()", code);
        Assert.DoesNotContain("new CSharpList<>()", code);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }
}
