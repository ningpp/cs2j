using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Code-generation regressions for the MSAGL test module: residual LINQ
/// terminals, erased compat conversions and operator/manual lowering shapes.
/// </summary>
public class TestModuleCodegenRegressionTests
{
    [Fact]
    public void ToHashSet_UsesCompatCollector()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using System.Linq;

            class Sample
            {
                public static HashSet<int> M(List<int> xs)
                {
                    return xs.ToHashSet();
                }
            }
            """);

        AssertConverted(result, "CSharpHashSet.toCSharpHashSet()");
    }

    [Fact]
    public void ToDictionary_UsesCompatCollector()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using System.Linq;

            class Item
            {
                public int Id;
                public string Name;
            }

            class Sample
            {
                static int Key(Item item) => item.Id;
                static string Value(Item item) => item.Name;

                public static Dictionary<int, string> M(List<Item> xs)
                {
                    return xs.ToDictionary(Key, Value);
                }
            }
            """);

        AssertConverted(result, "CSharpDictionary.toCSharpMap(");
    }

    [Fact]
    public void NonGenericIEnumerableCast_UsesFromAny()
    {
        var result = Convert("""
            using System.Collections;

            class Sample
            {
                public static IEnumerable M(object value)
                {
                    return (IEnumerable)value;
                }
            }
            """);

        AssertConverted(result, "CSharpGenericIterable.fromAny(");
    }

    [Fact]
    public void PrimitiveArray_IntoGenericIListSlot_UsesFromAny()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                static void Take(IList<int> xs) { }

                public static void M()
                {
                    Take(new int[] { 1, 2 });
                }
            }
            """);

        AssertConverted(result, "CSharpIList.fromAny(");
    }

    [Fact]
    public void CastArrayLength_IsParenthesized()
    {
        var result = Convert("""
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            class Sample
            {
                static object Get() { return null; }

                public static void M()
                {
                    Assert.AreEqual(0, ((string[])Get()).Length);
                }
            }
            """);

        AssertConverted(result, "((String[])");
        Assert.Contains(").length", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonDeserializeObjectReturn_IsCastToDeclaredType()
    {
        var result = Convert("""
            using System.Collections.Generic;

            namespace Newtonsoft.Json
            {
                public static class JsonConvert
                {
                    public static T DeserializeObject<T>(string json) { return default(T); }
                }
            }

            class Foo
            {
                public int Id;
            }

            class Sample
            {
                public static List<Foo> Load(string json)
                {
                    return Newtonsoft.Json.JsonConvert.DeserializeObject<List<Foo>>(json);
                }
            }
            """);

        AssertConverted(result, "(CSharpList<Foo>) JsonConvert.deserializeObject");
    }

    [Fact]
    public void IndexedSelectMany_UsesIndexedFlatMapFunction()
    {
        var result = Convert("""
            using System.Collections.Generic;
            using System.Linq;

            class Sample
            {
                static IEnumerable<int> Expand(int value, int index)
                {
                    return new int[] { value, index };
                }

                public static List<int> M(List<int> xs)
                {
                    return xs.SelectMany(Expand).ToList();
                }
            }
            """);

        AssertConverted(result, "IndexedFlatMapFunction");
    }

    [Fact]
    public void UserDefinedCompoundAssignment_LowersToStaticOperatorCall()
    {
        var result = Convert("""
            public struct ComplexLike
            {
                public double Value;

                public static ComplexLike operator +(ComplexLike left, ComplexLike right)
                {
                    return new ComplexLike { Value = left.Value + right.Value };
                }
            }

            class Sample
            {
                public static ComplexLike Sum(ComplexLike[] values)
                {
                    ComplexLike sum = new ComplexLike();
                    foreach (var value in values)
                    {
                        sum += value;
                    }
                    return sum;
                }
            }
            """);

        AssertConverted(result, "= ComplexLike.add(");
    }

    [Fact]
    public void EnumIsDefined_UsesEnumHelper()
    {
        var result = Convert("""
            using System;

            enum Mode { A, B }

            class Sample
            {
                public static bool M()
                {
                    return Enum.IsDefined(typeof(Mode), "A");
                }
            }
            """);

        AssertConverted(result, "EnumHelper.isDefined(");
        Assert.Contains("\"A\"", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparerDefault_UsesCompatComparer()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                public static IComparer<int> M()
                {
                    return Comparer<int>.Default;
                }
            }
            """);

        AssertConverted(result, "Comparer.getDefault()");
        Assert.Contains("import io.github.ningpp.compat.Comparer;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparerCreate_StaticReceiver_AddsCompatImport()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                public static SortedSet<int> M()
                {
                    return new SortedSet<int>(Comparer<int>.Create((a, b) => a - b));
                }
            }
            """);

        AssertConverted(result, "Comparer.create(");
        Assert.Contains("import io.github.ningpp.compat.Comparer;", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyListReturn_FromArray_WrapsCSharpListInCSharpIList()
    {
        var result = Convert("""
            using System.Collections.Generic;

            class Sample
            {
                public static IReadOnlyList<int> M(int[] values)
                {
                    return values;
                }
            }
            """);

        AssertConverted(result, "return CSharpIList.from(");
        Assert.Contains("import io.github.ningpp.compat.CSharpIList;", result.GeneratedCode, StringComparison.Ordinal);
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
