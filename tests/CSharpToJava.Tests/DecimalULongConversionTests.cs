using System.IO;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

public class DecimalULongConversionTests
{
    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Test.cs",
            Options = new ConversionOptions(),
        });
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx"))
                && Directory.Exists(Path.Combine(dir, "java", "csharptojava-compat")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    [Fact]
    public void NewDecimal_FromULong_UsesCompatHelper()
    {
        var result = Convert(@"
class C
{
    decimal M(ulong x)
    {
        return new decimal(x);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.Contains("Decimal.createFrom_long(", result.GeneratedCode, StringComparison.Ordinal);

        // The compat library must expose the helper used by the generated code.
        var repoRoot = FindRepoRoot();
        var compatDecimal = Path.Combine(repoRoot, "java", "csharptojava-compat", "src", "main", "java", "io", "github", "ningpp", "compat", "Decimal.java");
        Assert.True(File.Exists(compatDecimal), $"Compat source not found: {compatDecimal}");
        var compatSource = File.ReadAllText(compatDecimal);
        Assert.Contains("createFrom_long", compatSource, StringComparison.Ordinal);
    }
}
