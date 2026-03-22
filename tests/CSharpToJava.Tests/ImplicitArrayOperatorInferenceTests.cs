using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ImplicitArrayOperatorInferenceTests
{
    [Fact]
    public void ImplicitArray_WithOperatorExpressions_InfersElementType()
    {
        const string code = """
            namespace N {
                public struct Point {
                    public double X;
                    public double Y;
                    public Point(double x, double y) { X = x; Y = y; }
                    public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
                    public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
                }

                public class C {
                    public void M(Point start, Point end, Point s) {
                        var points = new [] { start + s, end, start - s };
                    }
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("new Point[]", result.GeneratedCode);
        Assert.DoesNotContain("new []", result.GeneratedCode);
    }
}
