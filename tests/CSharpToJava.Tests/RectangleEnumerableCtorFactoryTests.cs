using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class RectangleEnumerableCtorFactoryTests
{
    [Fact]
    public void RectangleCtorWithEnumerableOfRectangle_UsesFactoryMethod()
    {
        const string code = """
            using System.Collections.Generic;
            using Microsoft.Msagl.Core.Geometry;

            public class C {
                Rectangle M(IEnumerable<Rectangle> rs) {
                    return new Rectangle(rs);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("Rectangle.createFrom_Iterable_Rectangle(rs)", result.GeneratedCode);
        Assert.DoesNotContain("new Rectangle(rs)", result.GeneratedCode);
    }
}
