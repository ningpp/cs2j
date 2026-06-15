using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class ImplicitArrayInferenceTests
{
    [Fact]
    public async Task ProjectImplicitStringArray_WithUnresolvedConditionalAccessFirstElement_StaysStringArray()
    {
        var pipeline = new ProjectConversionPipeline(CreateProjectOptions());
        var results = await pipeline.ConvertProjectAsync(new[]
        {
            new SourceFile
            {
                FilePath = "Sample.cs",
                Content = @"
using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

public class MyClass
{
    public TestContext TestContext { get; set; }

    public string Resolve(string filePath)
    {
        var baseDirectories = new[] {
            TestContext?.DeploymentDirectory,
            string.IsNullOrEmpty(TestContext?.TestRunDirectory) ? null : Path.Combine(TestContext.TestRunDirectory, ""Out""),
            AppContext.BaseDirectory,
        };

        foreach (var baseDirectory in baseDirectories.Where(directory => !string.IsNullOrEmpty(directory))) {
            return Path.Combine(baseDirectory, filePath);
        }

        return filePath;
    }
}",
            },
        });

        var result = Assert.Single(results, item => item.FileName == "MyClass.java");
        var java = result.GeneratedCode;

        Assert.True(result.Success, java);
        Assert.Contains("new String[]", java);
        Assert.True(java.Contains("List<String> resolve_ProceduralLinq", StringComparison.Ordinal), java);
        Assert.DoesNotContain("new Object[]", java, StringComparison.Ordinal);
        Assert.DoesNotContain("List<Object> resolve_ProceduralLinq", java, StringComparison.Ordinal);
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
