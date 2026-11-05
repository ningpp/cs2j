using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Erasure bridges for compat collections:
/// - <c>CollectionUtilities.AddToMap&lt;TS,T,TC&gt;</c> instantiates its bucket in
///   the erased body, so the call site must publish the closed value type through
///   CollectionFactory (otherwise the fallback Set is cast to CSharpList/CSharpHashSet).
/// - <c>List&lt;T&gt;.ToArray()</c> inside a generic method erases to Object[]; the
///   result must be re-shaped with the runtime element type before a concrete
///   call site casts it to T[] (Object[] cannot be cast to VisibilityVertex[]).
/// </summary>
public class CollectionCastConversionTests
{
    private const string CollectionStub = """
        using System;
        using System.Collections.Generic;
        using Microsoft.Msagl.Core.DataStructures;

        namespace Microsoft.Msagl.Core.DataStructures
        {
            public class Set<T> : ICollection<T>
            {
                public int Count { get { return 0; } }
                public void Add(T value) { }
                public bool Remove(T value) { return true; }
                public bool Contains(T value) { return false; }
            }

            public class CollectionUtilities
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
        }
        """;

    [Fact]
    public void AddToMap_ListValue_PublishesValueType()
    {
        var result = Convert(CollectionStub + """

            class Sample
            {
                public static void Run()
                {
                    var dict = new Dictionary<string, List<int>>();
                    CollectionUtilities.AddToMap<int, string, List<int>>(dict, "key", 42);
                }
            }
            """);

        AssertConverted(result, "CollectionFactory.withValueType(CSharpList.class");
    }

    [Fact]
    public void AddToMap_HashSetValue_PublishesValueType()
    {
        var result = Convert(CollectionStub + """

            class Sample
            {
                public static void Run()
                {
                    var dict = new Dictionary<string, HashSet<string>>();
                    CollectionUtilities.AddToMap<string, string, HashSet<string>>(dict, "fruits", "apple");
                }
            }
            """);

        AssertConverted(result, "CollectionFactory.withValueType(CSharpHashSet.class");
    }

    [Fact]
    public void ListToArray_TypeParameterElement_UsesRuntimeTypedArray()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                private static T[] ToArray<T>(IEnumerable<T> source)
                {
                    if (source == null) return null;
                    var list = new List<T>();
                    foreach (var item in source) list.Add(item);
                    return list.ToArray();
                }
            }
            """);

        AssertConverted(result, "(T[]) CSharpArray.toTypedArrayRuntime(list.toArrayCs())");
    }

    [Fact]
    public void AddToMap_InMethodBody_MatchesBundlingShapes()
    {
        // Mirrors LinearMetroMapOrdering.Initialize/RadixSort: one HashSet bucket
        // and one List bucket; both are statement calls in a method body.
        var result = Convert(CollectionStub + """

            class MetroEdge
            {
            }

            class PathOnEdge
            {
            }

            class Sample
            {
                public static void Run()
                {
                    var adjacent = new Dictionary<int, HashSet<MetroEdge>>();
                    CollectionUtilities.AddToMap(adjacent, 1, new MetroEdge());

                    var r = new Dictionary<MetroEdge, List<PathOnEdge>>();
                    CollectionUtilities.AddToMap(r, new MetroEdge(), new PathOnEdge());
                }
            }
            """);

        AssertConverted(result, "CollectionFactory.withValueType(CSharpHashSet.class");
        Assert.Contains("CollectionFactory.withValueType(CSharpList.class", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static void AssertConverted(ConversionResult result, string expected)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains(expected, result.GeneratedCode, StringComparison.Ordinal);
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
