using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.ProjectTests;

public class UintKeywordMaxValueTests
{
    [Fact]
    public async Task UintKeywordMaxValue_EmitsLiteral()
    {
        var projectDir = Path.Combine(AppContext.BaseDirectory, "SampleProject");
        Assert.True(Directory.Exists(projectDir), $"SampleProject directory not found: {projectDir}");

        var options = new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
        };

        var pipeline = new ConversionPipeline();
        var results = await pipeline.ConvertProjectWithPartialMergeAsync(projectDir, options);

        Assert.NotEmpty(results);
        var result = Assert.Single(results, r =>
            string.Equals(r.FileName, "Sample.java", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));

        // uint.MaxValue and UInt32.MaxValue both emit 4294967295L
        var count = result.GeneratedCode.Split("4294967295L").Length - 1;
        Assert.Equal(2, count);
        Assert.DoesNotContain("Integer.MAX_VALUE", result.GeneratedCode, StringComparison.Ordinal);
    }
}
