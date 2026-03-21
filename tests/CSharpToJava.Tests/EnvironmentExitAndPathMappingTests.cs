using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class EnvironmentExitAndPathMappingTests
{
    [Fact]
    public void EnvironmentExit_And_PathCombineTempPath_AreMapped()
    {
        const string code = """
            using System;
            using System.IO;

            public class C {
                void M() {
                    Environment.Exit(1);
                    var p = Path.Combine(Path.GetTempPath(), "x");
                }
            }
            """;

        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest { SourceCode = code });

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.Message)));
        Assert.Contains("System.exit(1)", result.GeneratedCode);
        Assert.Contains("java.nio.file.Paths.get", result.GeneratedCode);
        Assert.Contains("System.getProperty(\"java.io.tmpdir\")", result.GeneratedCode);
    }
}
