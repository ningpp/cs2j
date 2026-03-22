using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SharedCompatibilityModuleTests
{
    [Fact]
    public void GenerateCompatibilitySupport_ShouldIncludeEqualityComparerShim()
    {
        var results = ProjectConversionPipeline.GenerateCompatibilitySupport("shared.compat", includeTestContext: true);

        var shim = results.First(r => r.FileName == "IEqualityComparer.java" && r.GeneratedCode.Contains("public interface IEqualityComparer<T>"));
        var stringHelper = results.First(r => r.FileName == "StringHelper.java");
        Assert.Contains("public interface IEqualityComparer<T>", shim.GeneratedCode);
        Assert.Contains("boolean equals(T x, T y);", shim.GeneratedCode);
        Assert.Contains("int hashCode(T obj);", shim.GeneratedCode);
        Assert.Contains("public static String concat(Object... values)", stringHelper.GeneratedCode);
        Assert.Contains("return \"\";", stringHelper.GeneratedCode);
    }

    [Fact]
    public void GenerateCompatibilitySupport_ShouldIncludeRegexAndTraceShims()
    {
        var results = ProjectConversionPipeline.GenerateCompatibilitySupport("shared.compat", includeTestContext: true);

        Assert.Contains(results, r => r.FileName == "Regex.java");
        Assert.Contains(results, r => r.FileName == "RegexOptions.java");
        Assert.Contains(results, r => r.FileName == "Match.java");
        Assert.Contains(results, r => r.FileName == "Group.java");
        Assert.Contains(results, r => r.FileName == "GroupCollection.java");
        Assert.Contains(results, r => r.FileName == "Trace.java");
        Assert.Contains(results, r => r.FileName == "DefaultTraceListener.java");
        Assert.Contains(results, r => r.FileName == "Assert.java");
        Assert.Contains(results, r => r.FileName == "CollectionAssert.java");
        var testContext = Assert.Single(results, r => r.FileName == "TestContext.java");
        Assert.Contains("public static final String TestDir = System.getProperty(\"user.dir\");", testContext.GeneratedCode);
        Assert.Contains("public static final String DeploymentDirectory = System.getProperty(\"user.dir\");", testContext.GeneratedCode);
        Assert.Contains("public static final String TestRunDirectory = System.getProperty(\"user.dir\");", testContext.GeneratedCode);
    }

    [Fact]
    public void ConversionPipeline_ShouldMapIEqualityComparerToCompatShim()
    {
        const string code = """
            using System.Collections.Generic;

            public class PointComparer : IEqualityComparer<string>
            {
                public bool Equals(string? x, string? y) => x == y;
                public int GetHashCode(string obj) => obj.GetHashCode();
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("import io.github.ningpp.compat.IEqualityComparer;", result.GeneratedCode);
        Assert.Contains("implements IEqualityComparer<String>", result.GeneratedCode);
        Assert.Contains("public boolean equals(String x, String y)", result.GeneratedCode);
        Assert.Contains("public int hashCode(String obj)", result.GeneratedCode);
    }

    [Fact]
    public async Task ConvertProjectWithSharedCompatibilityPackage_ShouldImportSharedPackageWithoutEmittingLocalHelpers()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-shared-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "Sample.cs"), """
                namespace Demo {
                    public class Sample {
                        public static bool TryGet(out int value) {
                            value = 42;
                            return true;
                        }

                        public int Read() {
                            int value;
                            TryGet(out value);
                            return value;
                        }
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var options = new ConversionOptions
            {
                EmitCompatibilityHelpers = false,
                SharedCompatibilityPackage = "shared.compat"
            };

            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempRoot, options);

            var sample = Assert.Single(results, r => r.FileName == "Sample.java");
            Assert.Contains("import shared.compat.*;", sample.GeneratedCode);
            Assert.Contains("IntHolder", sample.GeneratedCode);
            Assert.DoesNotContain(results, r => r.FileName == "IntHolder.java");
            Assert.DoesNotContain(results, r => r.FileName == "ObjectHolder.java");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}