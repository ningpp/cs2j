using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Tests that an interface indexer with a concrete return type preserves that
/// return type in the generated Java getter method, even when the syntax node
/// comes from a project-level merged declaration and semantic type info for the
/// indexer syntax is unavailable.
/// </summary>
public class InterfaceIndexerReturnTypeTests
{
    [Fact]
    public async Task InterfaceIndexer_WithConcreteReturnType_GeneratesTypedGetter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"cs2j-interface-indexer-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Point.cs"), @"
namespace Microsoft.Msagl.Core.Geometry
{
    public struct Point
    {
        public double X;
        public double Y;
    }
}
");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "ICurve.cs"), @"
namespace Microsoft.Msagl.Core.Geometry.Curves
{
    public interface ICurve
    {
        Point this[double t] { get; }
    }
}
");

            var options = new ConversionOptions
            {
                TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            };

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempDir, options);

            var curve = Assert.Single(results, r =>
                string.Equals(r.FileName, "ICurve.java", StringComparison.OrdinalIgnoreCase));
            Assert.True(curve.Success, string.Join("\n", curve.Diagnostics));
            Assert.Contains("Point get(double t)", curve.GeneratedCode, StringComparison.Ordinal);
            Assert.DoesNotContain("Object get(double t)", curve.GeneratedCode, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
