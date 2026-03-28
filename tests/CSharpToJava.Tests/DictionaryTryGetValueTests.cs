using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Dictionary.TryGetValue() conversion.
///
/// Bugs fixed:
///   1. TryGetValue(k, out existingVar)  — the already-declared variable case incorrectly
///      fell through to the generic out-parameter path, which injected an ObjectHolder and
///      produced the invalid call  dictionary.get(1, _d1Holder).
///      Fix: StatementTransformer.TransformIfStatement Fix 5 now also handles
///      IdentifierNameSyntax (existing variable), emitting
///      existingVar = dict.get(key) inside the containsKey branch.
/// </summary>
public class DictionaryTryGetValueTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── out var (new declaration) — regression guard ──────────────────────────

    [Fact]
    public void TryGetValue_OutVar_UsesContainsKeyAndGet()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    var dictionary = new Dictionary<int, string>();
                    if (dictionary.TryGetValue(1, out var value))
                    {
                        System.Console.WriteLine(value);
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("containsKey(1)", result.GeneratedCode);
        Assert.Contains(".get(1)", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("_valueHolder", result.GeneratedCode);
    }

    // ── out existingVar (pre-declared variable) — bug fix ────────────────────

    [Fact]
    public void TryGetValue_OutExistingVar_UsesContainsKeyAndAssignment()
    {
        const string code = """
            using System.Collections.Generic;
            class Point { }
            class C
            {
                public void DicTryGetValue()
                {
                    Point d1;
                    Dictionary<int, Point> dictionary = new Dictionary<int, Point>();
                    if (dictionary.TryGetValue(1, out d1))
                    {
                        System.Console.WriteLine(d1);
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must use containsKey as the guard
        Assert.Contains("containsKey(1)", result.GeneratedCode);
        // Must assign from get() inside the body (not pass _holder to get)
        Assert.Contains("d1 = dictionary.get(1)", result.GeneratedCode);
        // Must NOT produce ObjectHolder or pass _d1Holder to get()
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("_d1Holder", result.GeneratedCode);
        // get() must be called with exactly one argument (key only)
        Assert.DoesNotContain("get(1, ", result.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_OutExistingVar_WithElse_BothBranchesGenerated()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    string result;
                    var dict = new Dictionary<int, string>();
                    if (dict.TryGetValue(42, out result))
                    {
                        System.Console.WriteLine(result);
                    }
                    else
                    {
                        System.Console.WriteLine("not found");
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("containsKey(42)", result.GeneratedCode);
        Assert.Contains("result = dict.get(42)", result.GeneratedCode);
        Assert.Contains("not found", result.GeneratedCode);
        Assert.DoesNotContain("ObjectHolder", result.GeneratedCode);
        Assert.DoesNotContain("get(42, ", result.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_NegatedOutExistingVar_WithElse_GeneratesValidIfElse()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    string result;
                    var dict = new Dictionary<int, string>();
                    if (!dict.TryGetValue(42, out result))
                    {
                        System.Console.WriteLine("missing");
                    }
                    else
                    {
                        System.Console.WriteLine(result);
                    }
                }
            }
            """;

        var converted = Convert(code);

        Assert.True(converted.Success,
            $"Conversion failed:\n{string.Join("\n", converted.Diagnostics.Select(d => d.Message))}");
        Assert.Contains("result = dict.get(42)", converted.GeneratedCode);
        Assert.Matches(@"if\s*\(result\s*==\s*null\)\s*\{[\s\S]*?\}\s*else\s*\{", converted.GeneratedCode);
        Assert.DoesNotContain("get(42, ", converted.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_ChainedWithOr_DoesNotEmitGetWithOutParameter()
    {
        const string code = """
            using System.Collections.Generic;

            class Port { }
            class Shape { }
            class Couple { }

            class C
            {
                bool Test(Dictionary<Port, Shape> portsToShapes, Dictionary<Shape, Couple> couples, Port port)
                {
                    Shape portShape;
                    Couple boundaryCouple;

                    if (!portsToShapes.TryGetValue(port, out portShape)
                        || !couples.TryGetValue(portShape, out boundaryCouple))
                    {
                        return false;
                    }

                    return boundaryCouple != null;
                }
            }
            """;

        var converted = Convert(code);

        Assert.True(converted.Success,
            $"Conversion failed:\n{string.Join("\n", converted.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain(".get(port, ", converted.GeneratedCode);
        Assert.DoesNotContain(".get(portShape, ", converted.GeneratedCode);
        Assert.Contains("containsKey(port)", converted.GeneratedCode);
        Assert.Contains("containsKey(portShape)", converted.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_OutVarInConditionalExpression_DoesNotEmitTodoPlaceholder()
    {
        const string code = """
            using System.Collections.Generic;

            class C
            {
                string? Find(Dictionary<int, string> map, int key)
                {
                    return map.TryGetValue(key, out string value) ? value : null;
                }
            }
            """;

        var converted = Convert(code);

        Assert.True(converted.Success,
            $"Conversion failed:\n{string.Join("\n", converted.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("TODO: out var", converted.GeneratedCode);
        Assert.DoesNotContain("get(key,", converted.GeneratedCode);
    }

    // ── Standalone TryGetValue with struct (value type) — getOrDefault fix ───

    [Fact]
    public void TryGetValue_Standalone_StructValueType_UsesGetOrDefault()
    {
        const string code = """
            using System.Collections.Generic;
            namespace ConsoleApp1
            {
                public class Program
                {
                    public struct Point
                    {
                        public Point(double x, double y) : this()
                        {
                            this.X = x;
                            this.Y = y;
                        }
                        public double X { get; set; }
                        public double Y { get; set; }
                    }

                    public static void Main(string[] args)
                    {
                        var layerMap = new Dictionary<int, Point>();
                        for (int l = 0; l < 3; l++)
                        {
                            Point size;
                            layerMap.TryGetValue(l, out size);
                            layerMap[l] = new Point(3.14, size.X * size.Y);
                        }
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must use getOrDefault with new Program.Point() for struct value type
        Assert.Contains("getOrDefault(l, new Program.Point())", result.GeneratedCode);
        // Must NOT use bare .get() which returns null for missing keys
        Assert.DoesNotContain("= layerMap.get(l);", result.GeneratedCode);
    }

    [Fact]
    public void TryGetValue_Standalone_ReferenceType_UsesGet()
    {
        const string code = """
            using System.Collections.Generic;
            class C
            {
                public void Test()
                {
                    var dict = new Dictionary<int, string>();
                    string val;
                    dict.TryGetValue(1, out val);
                    System.Console.WriteLine(val);
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Reference types should use plain .get() since null is the C# default anyway
        Assert.Contains("dict.get(1)", result.GeneratedCode);
        Assert.DoesNotContain("getOrDefault", result.GeneratedCode);
    }
}
