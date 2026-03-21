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

    // ── Issue 4: byte/short parameter narrowing ──────────────────────────

    [Fact]
    public void ByteConstructorArg_IntLiteral_UsesExplicitCast()
    {
        // C# allows implicit narrowing of int literals to byte — Java requires explicit (byte) cast.
        const string code = """
            public class ByteDemo
            {
                public byte Color { get; set; }

                public ByteDemo(byte color)
                {
                    Color = color;
                }

                public static void Main(string[] args)
                {
                    System.Console.WriteLine(new ByteDemo(123));
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // The constructor call must use an explicit (byte) cast so Java compiles
        Assert.Contains("(byte)", java);
        Assert.Contains("new ByteDemo(", java);
    }

    [Fact]
    public void ShortMethodArg_IntLiteral_UsesExplicitCast()
    {
        // C# allows implicit narrowing of int literals to short — Java requires explicit (short) cast.
        const string code = """
            public class ShortDemo
            {
                public void Accept(short value) { }

                public void Test()
                {
                    Accept(1000);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("(short)", java);
        Assert.Contains("accept(", java);
    }

    // ── Issue 5: ArrayList(Collection) cannot accept array arguments ─────────────

    [Fact]
    public void ArrayArg_ToListConstructor_WrapsWithArraysAsList()
    {
        // C# allows passing an array to new List<T>(IEnumerable<T>).
        // Java's ArrayList constructor takes Collection, not an array.
        // The converter must wrap with Arrays.asList().
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test()
                {
                    string[] arr = new string[] { "a", "b" };
                    var list = new List<string>(arr);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Arrays.asList(", java);
        Assert.Contains("new ArrayList<", java);
    }

    [Fact]
    public void PrimitiveArrayArg_ToListConstructor_BoxesCorrectly()
    {
        // C# allows passing int[] to new List<int>(IEnumerable<int>).
        // Java needs boxing: Arrays.stream(arr).boxed().collect(Collectors.toList())
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test()
                {
                    int[] arr = new int[] { 1, 2, 3 };
                    var list = new List<int>(arr);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        // Primitive array needs boxing before it can be passed to ArrayList constructor
        Assert.True(
            java.Contains("Arrays.stream(") && java.Contains(".boxed()"),
            $"Expected primitive array boxing but got:\n{java}");
        Assert.Contains("new ArrayList<", java);
    }

    [Fact]
    public void ArrayArg_ToHashSetConstructor_WrapsWithArraysAsList()
    {
        // C# allows passing an array to new HashSet<T>(IEnumerable<T>).
        // Java's HashSet constructor takes Collection.
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test()
                {
                    string[] arr = new string[] { "a", "b" };
                    var set = new HashSet<string>(arr);
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Arrays.asList(", java);
        Assert.Contains("new HashSet<", java);
    }

    [Fact]
    public void ArrayDirectAssign_ToIListVar_WrapsWithArraysAsList()
    {
        // C# allows assigning a string[] to IList<string>.
        // Java does not allow assigning String[] to List<String>.
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test()
                {
                    string[] arr = new string[] { "a", "b" };
                    IList<string> items = arr;
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Arrays.asList(", java);
        // The variable type should be List<String> (or var) — not String[]
        Assert.DoesNotContain("String[] items", java);
    }

    [Fact]
    public void ArrayDirectAssign_ToICollectionVar_WrapsWithArraysAsList()
    {
        // C# allows assigning a string[] to ICollection<string>.
        // Java does not allow assigning String[] to Collection<String>.
        const string code = """
            using System.Collections.Generic;
            class C
            {
                void Test()
                {
                    string[] arr = new string[] { "a", "b" };
                    ICollection<string> items = arr;
                }
            }
            """;
        var java = ConvertAndGetCode(code);
        Assert.Contains("Arrays.asList(", java);
        Assert.DoesNotContain("String[] items", java);
    }
}
