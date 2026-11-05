using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// <c>CollectionUtilities.AddToMap&lt;TS,T,TC&gt;(dictionary, key, value)</c> must
/// pass the three call-site values into CollectionFactory instead of capturing
/// them in a no-arg lambda. Loop variables in an update expression
/// (<c>p = p.Next(), u = v</c>) are not effectively final, so a capturing lambda
/// is rejected by javac ("local variables referenced from a lambda expression
/// must be final or effectively final").
/// </summary>
public class CollectionFactoryCaptureConversionTests
{
    private const string CollectionStub = """
        using System;
        using System.Collections.Generic;
        using Microsoft.Msagl.Core.DataStructures;

        namespace Microsoft.Msagl.Core.DataStructures
        {
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
    public void AddToMap_HashSetValue_ReassignedLoopVariables_UsesValuePassingLambda()
    {
        // Mirrors MetroGraphData: the key variable is reassigned in the for
        // update expression, so it cannot be captured by the generated lambda.
        var result = Convert(CollectionStub + """

            class Sample
            {
                public static void Run(Dictionary<string, HashSet<string>> dict)
                {
                    string key = "start";
                    string value = "first";
                    for (int i = 0; i < 3; i++)
                    {
                        key = "k" + i;
                        value = "v" + i;
                        CollectionUtilities.AddToMap(dict, key, value);
                    }
                }
            }
            """);

        AssertConverted(result);
        Assert.Contains(
            "__a, __b, __c) -> CollectionUtilities.addToMap(__a, __b, __c)",
            result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("() -> CollectionUtilities.addToMap", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void AddToMap_ListValue_ReassignedLoopVariables_UsesValuePassingLambda()
    {
        var result = Convert(CollectionStub + """

            class Sample
            {
                public static void Run(Dictionary<string, List<int>> dict)
                {
                    string key = "start";
                    int value = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        key = "k" + i;
                        value = i;
                        CollectionUtilities.AddToMap(dict, key, value);
                    }
                }
            }
            """);

        AssertConverted(result);
        Assert.Contains(
            "__a, __b, __c) -> CollectionUtilities.addToMap(__a, __b, __c)",
            result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("() -> CollectionUtilities.addToMap", result.GeneratedCode, StringComparison.Ordinal);
    }

    private static void AssertConverted(ConversionResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        Assert.Contains("CollectionFactory.withValueType(", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains(", (__a, __b, __c) ->", result.GeneratedCode, StringComparison.Ordinal);
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
