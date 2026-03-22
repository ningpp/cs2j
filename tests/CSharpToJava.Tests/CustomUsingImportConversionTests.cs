using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CustomUsingImportConversionTests
{
    [Fact]
    public void NonSystemUsing_ShouldGeneratePackageWildcardImport()
    {
        const string code = """
            namespace A.Drawing
            {
                using A.Core;

                public interface IViewer
                {
                    Point Create(Point p);
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import A.Core.*;", result.GeneratedCode);
    }
}
