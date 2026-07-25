using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MultidimensionalArrayInitializerTests
{
    [Fact]
    public async Task RectangularStringArrayField_BareInitializer_EmitsCorrectJavaRanks()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "TestClass.cs",
                Content = @"
public class TestClass
{
    private static string[,] data = { { ""a"", ""b"" }, { ""c"", ""d"" } };
}
",
            },
        });

        var result = Assert.Single(results, item => item.FileName == "TestClass.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("new String[][]", java);
        Assert.DoesNotContain("new String[] {", java, StringComparison.Ordinal);
    }

    private static ConversionOptions CreateProjectOptions()
    {
        return new ConversionOptions
        {
            TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            PreferStreamApi = false,
        };
    }
}
