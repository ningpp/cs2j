using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ProjectPipelineUsingImportTests
{
    [Fact]
    public async Task ProjectConversion_ShouldKeepNonSystemUsingAsImport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "c2j-using-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var cs = Path.Combine(tempDir, "IViewer.cs");
            await File.WriteAllTextAsync(cs, """
                using Microsoft.Msagl.Core.Geometry;
                namespace Microsoft.Msagl.Drawing
                {
                    public interface IViewer
                    {
                        Point Create(Point p);
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var viewer = results.FirstOrDefault(r => r.FileName == "IViewer.java");
            Assert.NotNull(viewer);
            Assert.Contains("import Microsoft.Msagl.Core.Geometry.*;", viewer!.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
