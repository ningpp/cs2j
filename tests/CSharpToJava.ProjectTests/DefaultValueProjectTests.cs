using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.ProjectTests;

public class DefaultValueProjectTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleDefaultSolution");

    [Fact]
    public async Task LibProject_DefaultConvertedCorrectly()
    {
        var libDir = Path.Combine(FixtureDir, "SampleDefaultLib");
        Assert.True(Directory.Exists(libDir), $"Fixture not found: {libDir}");

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(libDir, options);

        Assert.NotEmpty(results);
        var result = Assert.Single(results, r =>
            string.Equals(r.FileName, "Defaults.java", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // struct / enum(0 值成员) / Flags / tuple 的 default 应被正确转换
        Assert.Contains("new Point()", result.GeneratedCode);
        Assert.Contains("Status.Full", result.GeneratedCode);
        Assert.Contains("0", result.GeneratedCode); // Permissions flags default
        Assert.Contains("Tuple.of(0, null)", result.GeneratedCode);
    }
}
