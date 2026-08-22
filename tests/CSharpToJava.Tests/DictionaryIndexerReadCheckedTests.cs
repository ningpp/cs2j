using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression (AGL SolverShellTests): C# Dictionary&lt;TKey, TValue&gt; indexer reads
/// (dict[key]) must throw KeyNotFoundException when the key is absent. The compat
/// library exposes getChecked(key) for that; indexer reads must emit it, while
/// TryGetValue-derived null-safe reads keep emitting get(key).
/// </summary>
public class DictionaryIndexerReadCheckedTests
{
    [Fact]
    public void DictionaryIndexerRead_EmitsGetChecked()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                private Dictionary<int, string> _map = new Dictionary<int, string>();

                string Read(int key)
                {
                    return _map[key];
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("_map.getChecked(key)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_map.get(key)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetValueRead_KeepsNullSafeGet()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                private Dictionary<int, string> _map = new Dictionary<int, string>();

                string Read(int key)
                {
                    return _map.TryGetValue(key, out var value) ? value : null;
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("_map.get(key)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("_map.getChecked(key)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void IDictionaryIndexerRead_EmitsGetChecked()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                string Read(IDictionary<int, string> map, int key)
                {
                    return map[key];
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("map.getChecked(key)", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentDictionaryIndexerRead_KeepsGet()
    {
        var result = Convert("""
            using System.Collections.Concurrent;

            class Sample
            {
                string Read(ConcurrentDictionary<int, string> map, int key)
                {
                    return map[key];
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("map.get(key)", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("getChecked", result.GeneratedCode, StringComparison.Ordinal);
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
