using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ConditionalStringInferenceTests
{
    [Fact]
    public async Task ProjectConditionalStringLocal_WithMSTestPropertyAndPathFallback_StaysString()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = """
                    using System.IO;
                    using Microsoft.VisualStudio.TestTools.UnitTesting;

                    public class Sample
                    {
                        public TestContext TestContext { get; set; }

                        string GetGeomGraphFileName(string graphName)
                        {
                            var dirName = (null != this.TestContext) ? this.TestContext.DeploymentDirectory : Path.GetTempPath();
                            return Path.Combine(dirName, graphName);
                        }
                    }
                    """,
            },
        });

        var result = Assert.Single(results, item => item.FileName == "Sample.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("String dirName =", java, StringComparison.Ordinal);
        Assert.Contains("java.nio.file.Paths.get(dirName, graphName).toString()", java, StringComparison.Ordinal);
        Assert.DoesNotContain("Object dirName", java, StringComparison.Ordinal);
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
