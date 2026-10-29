using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# Enumerable.Any() (no predicate) maps to anyMatch, but the
/// java.util.stream shape requires a Predicate; compat CSharpGenericIterable
/// supplies a no-arg default so the emitted call resolves on the erased
/// iterable carrier (mvn 现场: AGL tools/ArgsParser UsageString
/// "unknownOptions.anyMatch()").
/// </summary>
public class EnumerableAnyNoPredicateTests
{
    [Fact]
    public void AnyWithoutPredicate_EmitsNoArgAnyMatch()
    {
        var result = Convert("""
using System.Collections.Generic;
using System.Linq;
class C
{
    string Describe(IEnumerable<string> unknownOptions)
    {
        if (unknownOptions.Any())
            return "Unknown options:";
        return string.Empty;
    }
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";
        Assert.Contains(".anyMatch()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatIterable_ProvidesNoArgAnyMatch()
    {
        var compatPath = Path.Combine(
            FindRepoRoot(),
            "java", "csharptojava-compat", "src", "main", "java",
            "io", "github", "ningpp", "compat", "CSharpGenericIterable.java");
        var source = File.ReadAllText(compatPath);

        Assert.Contains("default boolean anyMatch()", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "CSharpToJavaConverter.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new DirectoryNotFoundException("Could not locate repo root.");
    }

    private static ConversionResult Convert(string sourceCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
    }
}
