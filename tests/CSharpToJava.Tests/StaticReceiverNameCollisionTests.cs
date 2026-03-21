using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class StaticReceiverNameCollisionTests
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

    [Fact]
    public void StaticTypeReceiver_WithSameNamedField_IsFullyQualified()
    {
        const string code = """
            namespace Demo {
                public class Point {
                    public static bool GreaterThanOrEqual(Point a, Point b) => true;
                }

                public class VisibilityVertex {
                    public readonly Point Point;

                    public VisibilityVertex(Point p) {
                        Point = p;
                    }

                    public static bool Compare(Point a, Point b) {
                        return Point.GreaterThanOrEqual(a, b);
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("return Point.greaterThanOrEqual(a, b);", java);
        Assert.Contains("return Demo.Point.greaterThanOrEqual(a, b);", java);
    }

    [Fact]
    public void UserDefinedOperatorReceiver_WithSameNamedField_IsFullyQualified()
    {
        const string code = """
            namespace Demo {
                public class Point {
                    public static bool operator >=(Point a, Point b) => true;
                    public static bool operator <=(Point a, Point b) => true;
                }

                public class VisibilityVertex {
                    public readonly Point Point;

                    public VisibilityVertex(Point p) {
                        Point = p;
                    }

                    public static bool Compare(Point a, Point b) {
                        return a >= b;
                    }
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("return Point.greaterThanOrEqual(a, b);", java);
        Assert.Contains("return Demo.Point.greaterThanOrEqual(a, b);", java);
    }

    [Fact]
    public void StaticReceiver_InGlobalNamespace_DoesNotEmitGlobalNamespaceToken()
    {
        const string code = """
            public class XmlReader {
                public static XmlReader Create(XmlReader r) => r;
            }

            public class ReaderHolder {
                private XmlReader XmlReader;

                public ReaderHolder(XmlReader r) {
                    XmlReader = r;
                }

                public XmlReader Build(XmlReader r) {
                    return XmlReader.Create(r);
                }
            }
            """;

        var java = ConvertCode(code);

        Assert.DoesNotContain("<global namespace>", java);
        Assert.Contains("return XmlReader.create(r);", java);
    }
}
