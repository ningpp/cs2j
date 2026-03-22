using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class SharedCompatibilityModuleTests
{
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