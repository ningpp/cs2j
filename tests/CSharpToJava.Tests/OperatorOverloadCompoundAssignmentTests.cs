using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that compound assignments (+=, -=, *=, etc.) on types with user-defined
/// operator overloads are correctly expanded to static operator-method calls in Java.
/// </summary>
public class OperatorOverloadCompoundAssignmentTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // Property LHS: LeftTop += new Point2() inside a property setter
    // Expected: setLeftTop(Point2.add(getLeftTop(), new Point2()))
    [Fact]
    public void CompoundAdd_OnPropertyLhs_InsidePropertySetter_EmitsGetterSetterAndStaticCall()
    {
        const string code = """
            public class Point2 {
                public int X;
                public int Y;
                public static Point2 operator+(Point2 a, Point2 b) => new Point2();
            }

            public class Rect {
                public Point2 LeftTop { get; set; }
                public Point2 Center
                {
                    get { return new Point2(); }
                    set
                    {
                        LeftTop += new Point2();
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // Must NOT emit raw Java += on a reference type
        Assert.DoesNotContain("leftTop += ", result.GeneratedCode);
        // Must expand to getter + static call + setter
        Assert.Contains("getLeftTop()", result.GeneratedCode);
        Assert.Contains("Point2.add(", result.GeneratedCode);
        Assert.Contains("setLeftTop(", result.GeneratedCode);
    }

    // Field LHS (member access): this.pos += delta
    // Expected: this.pos = Point2.add(this.pos, delta)
    [Fact]
    public void CompoundAdd_OnFieldMemberAccessLhs_EmitsExplicitAssignmentWithStaticCall()
    {
        const string code = """
            public class Point2 {
                public int X;
                public static Point2 operator+(Point2 a, Point2 b) => new Point2();
            }

            public class Shape {
                private Point2 pos = new Point2();
                public void Translate(Point2 delta) {
                    this.pos += delta;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("this.pos += ", result.GeneratedCode);
        Assert.Contains("Point2.add(this.pos, delta)", result.GeneratedCode);
    }

    // Local variable LHS: pt += a
    // Expected: pt = Point2.add(pt, a)
    [Fact]
    public void CompoundAdd_OnLocalVariableLhs_EmitsExplicitAssignment()
    {
        const string code = """
            public class Point2 {
                public static Point2 operator+(Point2 a, Point2 b) => new Point2();
            }

            public class Demo {
                public Point2 Sum(Point2 a, Point2 b) {
                    Point2 pt = new Point2();
                    pt += a;
                    pt += b;
                    return pt;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("pt += ", result.GeneratedCode);
        Assert.Contains("Point2.add(pt,", result.GeneratedCode);
    }

    // Subtraction compound assignment -=
    [Fact]
    public void CompoundSubtract_OnLocalVariable_EmitsStaticSubtractCall()
    {
        const string code = """
            public class Vec {
                public static Vec operator-(Vec a, Vec b) => new Vec();
            }

            public class Demo {
                public Vec Run(Vec a, Vec b) {
                    a -= b;
                    return a;
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success, $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        Assert.DoesNotContain("a -= ", result.GeneratedCode);
        Assert.Contains("Vec.subtract(a,", result.GeneratedCode);
    }
}
