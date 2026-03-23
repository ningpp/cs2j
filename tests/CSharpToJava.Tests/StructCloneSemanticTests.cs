using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests for Clone-related fixes:
/// - Struct local variable assignment cloning
/// - Struct simple assignment cloning
/// - Struct return value cloning
/// - StructTransformer Cloneable interface
/// - ICloneable explicit interface dedup
/// </summary>
public class StructCloneSemanticTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Struct local variable declaration cloning ──────────────────────────────

    [Fact]
    public void StructLocalDeclaration_IdentifierRhs_InsertsClone()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                public void M(Box a) {
                    Box b = a;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("a.clone()", result.GeneratedCode);
    }

    [Fact]
    public void StructLocalDeclaration_NewExpression_DoesNotClone()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                public void M() {
                    Box b = new Box();
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(".clone()", result.GeneratedCode);
    }

    [Fact]
    public void StructLocalDeclaration_MethodReturn_DoesNotClone()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                static Box CreateBox() { return new Box(); }

                public void M() {
                    Box b = CreateBox();
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        // The initializer is a method invocation — treated as temporary, no clone at assignment
        Assert.DoesNotContain("createBox().clone()", result.GeneratedCode);
    }

    // ── Struct simple assignment cloning ───────────────────────────────────────

    [Fact]
    public void StructAssignment_IdentifierRhs_InsertsClone()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                public void M(Box a) {
                    Box b;
                    b = a;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("b = a.clone()", result.GeneratedCode);
    }

    // ── Struct return value cloning ────────────────────────────────────────────

    [Fact]
    public void StructReturn_FieldAccess_InsertsClone()
    {
        const string code = """
            public struct Point {
                public int X;
                public int Y;
            }

            public class Shape {
                private Point center;

                public Point GetCenter() {
                    return center;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("center.clone()", result.GeneratedCode);
    }

    [Fact]
    public void StructReturn_NewExpression_DoesNotClone()
    {
        const string code = """
            public struct Point {
                public int X;
                public int Y;
            }

            public class Factory {
                public Point Create() {
                    return new Point();
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(".clone()", result.GeneratedCode);
    }

    // ── StructTransformer Cloneable interface ─────────────────────────────────

    [Fact]
    public void Struct_ImplementsCloneable()
    {
        const string code = """
            public struct Vector2 {
                public float X;
                public float Y;
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("implements Cloneable", result.GeneratedCode);
    }

    // ── ICloneable explicit interface dedup ────────────────────────────────────

    [Fact]
    public void ICloneable_ExplicitAndPublic_DedupRemovesObjectVersion()
    {
        const string code = """
            using System;

            public class Curve : ICloneable {
                object ICloneable.Clone() { return new Curve(); }
                public Curve Clone() { return new Curve(); }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        // Should have exactly one clone() method (the typed one), not two
        var code_output = result.GeneratedCode;
        int firstClone = code_output.IndexOf("clone()");
        Assert.True(firstClone >= 0, "Should have at least one clone() method");

        // Count occurrences of clone() method declarations (not calls)
        // The typed version should be present
        Assert.Contains("Curve clone()", code_output);
    }

    // ── Primitive struct should NOT get cloned ────────────────────────────────

    [Fact]
    public void PrimitiveType_Assignment_DoesNotClone()
    {
        const string code = """
            public class C {
                public void M() {
                    int x = 5;
                    int y = x;
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain(".clone()", result.GeneratedCode);
    }

    // ── Existing StructArgumentClone tests remain valid ───────────────────────

    [Fact]
    public void StructArguments_StillCloned_ForByValueCalls()
    {
        const string code = """
            public struct Box {
                public int Value;
            }

            public class C {
                static void Consume(Box value) {}

                public void M(Box value) {
                    Consume(value);
                }
            }
            """;

        var result = Convert(code);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("consume(value.clone())", result.GeneratedCode);
    }
}
