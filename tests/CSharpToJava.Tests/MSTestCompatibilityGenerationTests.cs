using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class MSTestCompatibilityGenerationTests
{
    [Fact]
    public async Task ConvertProjectWithPartialMergeAsync_MSTestTestContext_EmitsCompatibilityStub()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "c2j-mstest-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "MsaglTestBase.cs"), """
                using Microsoft.VisualStudio.TestTools.UnitTesting;

                namespace Sample.Tests
                {
                    public class MsaglTestBase
                    {
                        public TestContext TestContext { get; set; }

                        public void Log()
                        {
                            TestContext.WriteLine("hello {0}", 1);
                        }
                    }
                }
                """);

            var pipeline = new ConversionPipeline();
            var results = await pipeline.ConvertProjectWithPartialMergeAsync(tempRoot, new ConversionOptions());
            var shim = results.Single(r => r.FileName == "TestContext.java");

            Assert.Equal("Microsoft.VisualStudio.TestTools.UnitTesting", shim.Package);
            Assert.Contains("public class TestContext", shim.GeneratedCode);
            Assert.Contains("public static void writeLine(String format, Object... args)", shim.GeneratedCode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}