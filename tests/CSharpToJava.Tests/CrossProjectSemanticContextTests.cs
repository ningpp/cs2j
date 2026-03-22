using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class CrossProjectSemanticContextTests
{
    [Fact]
    public async Task ConvertProjectWithSemanticReferences_ShouldResolveReferencedProjectProperties()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-cross-project-" + Guid.NewGuid().ToString("N"));
        var referencedDir = Path.Combine(tempRoot, "Referenced");
        var mainDir = Path.Combine(tempRoot, "Main");
        Directory.CreateDirectory(referencedDir);
        Directory.CreateDirectory(mainDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(referencedDir, "GeoEdge.cs"), """
                namespace RefNs
                {
                    public class GeoEdge
                    {
                        public int EdgeGeometry { get; set; }
                    }
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(mainDir, "Edge.cs"), """
                using RefNs;

                namespace MainNs
                {
                    public class Edge
                    {
                        GeoEdge geometryEdge = new GeoEdge();

                        public int Value
                        {
                            get { return geometryEdge.EdgeGeometry; }
                        }
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                mainDir,
                options,
                new[] { referencedDir });

            var edgeResult = results.FirstOrDefault(r => r.FileName == "Edge.java");
            Assert.NotNull(edgeResult);
            Assert.Contains("geometryEdge.getEdgeGeometry()", edgeResult!.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ConvertProjectWithSemanticReferences_ShouldStillEmitEnumsFromPrimaryProject()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-cross-project-enum-" + Guid.NewGuid().ToString("N"));
        var referencedDir = Path.Combine(tempRoot, "Referenced");
        var mainDir = Path.Combine(tempRoot, "Main");
        Directory.CreateDirectory(referencedDir);
        Directory.CreateDirectory(mainDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(referencedDir, "Helper.cs"), """
                namespace RefNs
                {
                    public class Helper {}
                }
                """);

            await File.WriteAllTextAsync(Path.Combine(mainDir, "Color.cs"), """
                namespace MainNs
                {
                    public enum Color
                    {
                        Red,
                        Blue
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions();

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(
                mainDir,
                options,
                new[] { referencedDir });

            Assert.Contains(results, r => r.FileName == "Color.java");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
