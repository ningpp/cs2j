using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for three Java type conversion errors:
/// 1. Iterable cannot be converted to Collection — method args need Collection but receive Iterable
/// 2. Object[] cannot be converted to Iterable — arrays passed where Iterable/Collection expected
/// 3. Object[] cannot be converted to int[] — Stream.toArray() returns Object[] instead of typed array
/// </summary>
public class TypeCoercionTests
{
    private static string ConvertAndGetCode(string code, ConversionOptions? options = null)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = options ?? new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java17,
                PreferStreamApi = true,
            }
        });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Issue 1: Iterable cannot be converted to Collection ──────────────

    [Fact]
    public void AddRange_WithIEnumerableArg_WrapsForCollectionParam()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test(List<string> target, IEnumerable<string> source)
                {
                    target.AddRange(source);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // addAll requires Collection, IEnumerable maps to Iterable — must be wrapped
        Assert.Contains("addAll(", java);
        // Should NOT just pass the Iterable directly (that won't compile)
        // The wrapper should involve StreamSupport or ArrayList constructor
        Assert.True(
            java.Contains("StreamSupport.stream(") || java.Contains("new ArrayList<>("),
            $"Expected Iterable->Collection wrapper but got:\n{java}");
    }

    // ── Issue 2: Object[] cannot be converted to Iterable ────────────────

    [Fact]
    public void ArrayArg_WhereParameterExpectsIEnumerable_WrapsWithArraysAsList()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Consume(IEnumerable<string> items) { }
                void Test()
                {
                    string[] arr = new string[] { "a", "b" };
                    Consume(arr);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Array passed to IEnumerable param: should be wrapped with Arrays.asList()
        Assert.Contains("Arrays.asList(", java);
    }

    [Fact]
    public void PrimitiveArrayArg_WhereParameterExpectsIEnumerable_BoxesCorrectly()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Consume(IEnumerable<int> items) { }
                void Test()
                {
                    int[] arr = new int[] { 1, 2, 3 };
                    Consume(arr);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Primitive array passed to IEnumerable<int> (Iterable<Integer>): needs boxing
        Assert.True(
            java.Contains("Arrays.stream(") && java.Contains(".boxed()"),
            $"Expected primitive array boxing but got:\n{java}");
    }

    // ── Issue 3: Object[] cannot be converted to int[] ──────────────────

    [Fact]
    public void ToArray_OnIntStream_ProducesTypedIntArray()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                void Test(List<int> numbers)
                {
                    var result = numbers.Select(x => x * 2).ToArray();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Stream<Integer>.toArray() returns Object[] — must use mapToInt().toArray() for int[]
        Assert.Contains("mapToInt(", java);
        Assert.Contains(".toArray()", java);
        Assert.DoesNotContain("new Object[]", java);
    }

    [Fact]
    public void ToArray_OnStringStream_ProducesTypedStringArray()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                void Test(List<string> names)
                {
                    var result = names.Where(n => n.Length > 3).ToArray();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Stream<String>.toArray() returns Object[] — must use .toArray(String[]::new)
        Assert.Contains("String[]::new", java);
    }

    [Fact]
    public void ToArray_OnLongStream_ProducesTypedLongArray()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                void Test(List<long> values)
                {
                    var result = values.Select(x => x + 1).ToArray();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should use mapToLong for long[]
        Assert.Contains("mapToLong(", java);
    }

    [Fact]
    public void ToArray_OnDoubleStream_ProducesTypedDoubleArray()
    {
        const string code = """
            using System.Collections.Generic;
            using System.Linq;
            class C
            {
                void Test(List<double> values)
                {
                    var result = values.Select(x => x * 1.5).ToArray();
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Should use mapToDouble for double[]
        Assert.Contains("mapToDouble(", java);
    }

    // ── Implicit array type inference ────────────────────────────────────

    [Fact]
    public void ImplicitArrayCreation_WithInts_InfersIntNotObject()
    {
        const string code = """
            class C
            {
                void Test()
                {
                    var arr = new[] { 1, 2, 3 };
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // new[] { 1, 2, 3 } should become new int[] { 1, 2, 3 }, not new Object[]
        Assert.Contains("new int[]", java);
        Assert.DoesNotContain("new Object[]", java);
    }

    [Fact]
    public void ImplicitArrayCreation_WithStrings_InfersStringNotObject()
    {
        const string code = """
            class C
            {
                void Test()
                {
                    var arr = new[] { "a", "b", "c" };
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("new String[]", java);
        Assert.DoesNotContain("new Object[]", java);
    }
}
