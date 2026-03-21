using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for StructTransformer — covers the issues and fixes documented in
/// docs/transformers/type/StructTransformer.md.
/// </summary>
public class StructTransformerTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Fix 5 ──────────────────────────────────────────────────────────────────
    // Verify that 'static readonly' fields inside a struct keep both the 'static'
    // and 'final' modifiers after the member-dispatch loop processes them.
    // This guards against the modifier list assembly bug described in Issue 5.

    [Fact]
    public void Struct_StaticReadonlyField_PreservesBothStaticAndFinalModifiers()
    {
        const string code = """
            public struct Counter {
                public static readonly int MaxCount = 100;
                public int Value;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // The generated Java field must be 'public static final int MaxCount'
        Assert.Contains("static final int MaxCount", result.GeneratedCode);
    }

    // ── Fix 2 ──────────────────────────────────────────────────────────────────
    // 'readonly struct' should NOT make the class final; it should make instance
    // fields final.

    [Fact]
    public void ReadonlyStruct_AppliesFinalToFields_NotToClass()
    {
        const string code = """
            public readonly struct ImmutablePoint {
                public int X;
                public int Y;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        // Class declaration must NOT be "final class"
        Assert.DoesNotContain("final class ImmutablePoint", result.GeneratedCode);
        // Instance fields must be declared final
        Assert.Contains("final int X", result.GeneratedCode);
        Assert.Contains("final int Y", result.GeneratedCode);
    }

    // ── Fix 3 ──────────────────────────────────────────────────────────────────
    // 'ref struct' should emit a leading comment warning about Java not
    // enforcing stack allocation.

    [Fact]
    public void RefStruct_EmitsStackOnlyComment()
    {
        const string code = """
            public ref struct SpanLike {
                public int Length;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("ref struct", result.GeneratedCode);
    }

    // ── Fix 4 ──────────────────────────────────────────────────────────────────
    // When a C# struct has explicit parameterised constructors but no no-arg
    // constructor, the transformer must emit a zero-initialising no-arg ctor.

    [Fact]
    public void Struct_WithExplicitCtor_EmitsZeroArgCtor()
    {
        const string code = """
            public struct Point {
                public int X;
                public int Y;
                public Point(int x, int y) { X = x; Y = y; }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        // A no-arg constructor should be present
        Assert.Contains("Point()", result.GeneratedCode);
        // Its body must mention zero-initialization
        Assert.Contains("zero-initialized", result.GeneratedCode);
    }

    [Fact]
    public void Struct_WithFinalFields_AndExplicitCtor_NoArgCtorInitializesFinals()
    {
        const string code = """
            public struct Pair {
                public readonly int A;
                public readonly string B;

                public Pair(int a, string b) {
                    A = a;
                    B = b;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("public Pair()", result.GeneratedCode);
        Assert.Contains("this.A = 0;", result.GeneratedCode);
        Assert.Contains("this.B = null;", result.GeneratedCode);
    }

    // ── Fix 1 ──────────────────────────────────────────────────────────────────
    // Every generated struct class must have a clone() method and a Javadoc
    // comment warning about value semantics.

    [Fact]
    public void Struct_EmitsCloneMethod()
    {
        const string code = """
            public struct Vector2 {
                public float X;
                public float Y;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("clone()", result.GeneratedCode);
    }

    [Fact]
    public void Struct_WithReadonlyFields_CloneUsesConstructorCopyWithoutFinalAssignments()
    {
        const string code = """
            public struct ConstraintListForVariable {
                public readonly System.Collections.Generic.List<int> Constraints;
                public readonly int NumberOfLeftConstraints;

                public ConstraintListForVariable(System.Collections.Generic.List<int> constraints, int numberOfLeftConstraints) {
                    Constraints = constraints;
                    NumberOfLeftConstraints = numberOfLeftConstraints;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("return new ConstraintListForVariable(this.Constraints, this.NumberOfLeftConstraints);", result.GeneratedCode);
        Assert.DoesNotContain("copy.Constraints = this.Constraints;", result.GeneratedCode);
        Assert.DoesNotContain("copy.NumberOfLeftConstraints = this.NumberOfLeftConstraints;", result.GeneratedCode);
    }

    [Fact]
    public void Struct_EmitsValueSemanticsJavadoc()
    {
        const string code = """
            public struct Vector2 {
                public float X;
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("value type", result.GeneratedCode);
    }

    // ── Fix 6 ──────────────────────────────────────────────────────────────────
    // When operator== generates an equals() method, a hashCode() must also be
    // emitted to satisfy the Java equals/hashCode contract.

    [Fact]
    public void Struct_WithEqualityOperator_EmitsHashCode()
    {
        const string code = """
            public struct Color {
                public byte R;
                public byte G;
                public byte B;
                public static bool operator ==(Color a, Color b) => a.R == b.R && a.G == b.G && a.B == b.B;
                public static bool operator !=(Color a, Color b) => !(a == b);
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success);
        Assert.Contains("hashCode", result.GeneratedCode);
    }
}
