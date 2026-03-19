using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for calling C# value-type instance methods (GetHashCode, CompareTo, ToString)
/// on Java primitive variables. Java primitives (int, long, double ...) cannot call instance methods,
/// so the converter must emit the corresponding static wrapper form instead.
///
/// Errors observed in generated Java output (e.g. "无法取消引用int"):
///   int source; source.GetHashCode()   → source.hashCode()   ❌ (cannot dereference int)
///   int o1, o2; o1.CompareTo(o2)       → o1.compareTo(o2)   ❌
///   int dir;    dir.ToString()          → dir.toString()      ❌
///
/// Expected correct Java:
///   Integer.hashCode(source)  ✓
///   Integer.compare(o1, o2)   ✓
///   String.valueOf(dir)        ✓  (or Integer.toString(dir))
/// </summary>
public class PrimitiveMethodCallTests
{
    private static string ConvertCode(string code)
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = code,
            Options = new ConversionOptions
            {
                TargetJavaVersion = JavaVersion.Java17,
            }
        });
        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        return result.GeneratedCode;
    }

    // ── Test 1: int.GetHashCode() on a field (LayerEdge pattern) ──────────────
    // C#:   uint hc = (uint)source.GetHashCode();
    // Java: int hc = (int)(Integer.hashCode(source));
    [Fact]
    public void IntGetHashCode_OnField_EmitsStaticWrapperForm()
    {
        const string code = """
            namespace Test {
                public class LayerEdge {
                    int source;
                    public override int GetHashCode() {
                        uint hc = (uint)source.GetHashCode();
                        return (int)((hc << 5 | hc >> 27) + (uint)source);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT call .hashCode() on a primitive int field
        Assert.DoesNotContain("source.hashCode()", java);
        // Must use static wrapper form
        Assert.Contains("Integer.hashCode(source)", java);
    }

    // ── Test 2: int.CompareTo(other) on local variable (MetroMapOrdering pattern) ──
    // C#:   return (o1.CompareTo(o2));
    // Java: return (Integer.compare(o1, o2));
    [Fact]
    public void IntCompareTo_OnLocalVariable_EmitsStaticCompareForm()
    {
        const string code = """
            namespace Test {
                public class Comparer {
                    public int Compare(int o1, int o2) {
                        return o1.CompareTo(o2);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT call .compareTo() on primitive int
        Assert.DoesNotContain("o1.compareTo(o2)", java);
        // Must use Integer.compare static form
        Assert.Contains("Integer.compare(o1, o2)", java);
    }

    // ── Test 3: long.GetHashCode() on a field ─────────────────────────────────
    [Fact]
    public void LongGetHashCode_OnField_EmitsStaticWrapperForm()
    {
        const string code = """
            namespace Test {
                public class Edge {
                    long id;
                    public override int GetHashCode() {
                        return id.GetHashCode();
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("id.hashCode()", java);
        Assert.Contains("Long.hashCode(id)", java);
    }

    // ── Test 4: double.ToString() on field (ScanDirection pattern) ─────────────
    // C#:   return getDirection().ToString();  — direction is int (Direction enum value stored as int)
    // More directly: int field.ToString()
    [Fact]
    public void IntToString_OnLocalVariable_EmitsStringValueOf()
    {
        const string code = """
            using System;
            namespace Test {
                public class ScanDir {
                    int direction;
                    public string DirectionName() {
                        return direction.ToString();
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        // Must NOT call .toString() on int
        Assert.DoesNotContain("direction.toString()", java);
        // Must use static form
        Assert.True(
            java.Contains("String.valueOf(direction)") || java.Contains("Integer.toString(direction)"),
            $"Expected String.valueOf(direction) or Integer.toString(direction), got:\n{java}");
    }

    // ── Test 5: CompareTo on int parameter in lambda ────────────────────────────
    // C#:   node1.CompareTo(node2)  where node1, node2 are int parameters
    [Fact]
    public void IntCompareTo_OnParameter_EmitsStaticCompareForm()
    {
        const string code = """
            using System.Collections.Generic;
            namespace Test {
                public class Sorter {
                    public void Sort(int node1, int node2) {
                        int cmp = node1.CompareTo(node2);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("node1.compareTo(node2)", java);
        Assert.Contains("Integer.compare(node1, node2)", java);
    }

    // ── Test 6: GetHashCode used inside actual GetHashCode override (self-check) ──
    // This test reflects the exact pattern in LayerEdge.cs:
    // - class with int fields source, target
    // - GetHashCode() override that calls source.GetHashCode()
    [Fact]
    public void GetHashCode_Override_WithIntFields_CompilesSafely()
    {
        const string code = """
            namespace Test {
                public class Pair {
                    int x;
                    int y;
                    public override int GetHashCode() {
                        int hc = x.GetHashCode();
                        return hc + y.GetHashCode();
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("x.hashCode()", java);
        Assert.DoesNotContain("y.hashCode()", java);
        Assert.Contains("Integer.hashCode(x)", java);
        Assert.Contains("Integer.hashCode(y)", java);
    }
}
