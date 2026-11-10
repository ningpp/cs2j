using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Merging the DEBUG/global::/pattern syntax-tree rewrites into one
/// normalization step must not change conversion output. These tests pin the
/// observable behaviour, plus the new pipeline contract (no separate pattern
/// rewrite pass).
/// </summary>
public class ProjectCompilationNormalizationTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;

        namespace Demo
        {
            public class Sample
            {
                public string Describe(object value)
                {
                    var text = global::System.String.Concat("a", "b");
        #if DEBUG
                    text += "debugOnlyMarker";
        #endif
                    if (value is List<int> items)
                    {
                        return text + items.Count;
                    }
                    return text;
                }
            }
        }
        """;

    private static ConversionOptions CreateOptions() => new()
    {
        TypeMappingConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    private static CSharpCompilation CreateCompilation(bool withDebug)
    {
        var parseOptions = withDebug
            ? new CSharpParseOptions().WithPreprocessorSymbols("DEBUG")
            : new CSharpParseOptions();
        var tree = CSharpSyntaxTree.ParseText(Source, parseOptions, "Sample.cs");
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        Assert.False(string.IsNullOrEmpty(trustedAssemblies));
        var references = trustedAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        return CSharpCompilation.Create(
            "NormalizationTests",
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConvertProjectAsync_NormalizesDebugGlobalAliasAndPattern(bool withDebug)
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        var results = await pipeline.ConvertProjectAsync(CreateCompilation(withDebug));

        var result = Assert.Single(results, item => item.FileName == "Sample.java");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.DoesNotContain("global::", result.GeneratedCode, StringComparison.Ordinal);
        Assert.DoesNotContain("debugOnlyMarker", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("class Sample", result.GeneratedCode, StringComparison.Ordinal);
        Assert.Contains("items", result.GeneratedCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertProjectAsync_RunsAllTreeRewritesBeforePasses()
    {
        var pipeline = new ProjectConversionPipeline(CreateOptions());
        await pipeline.ConvertProjectAsync(CreateCompilation(withDebug: true));

        // The pattern rewrite is now part of the single up-front normalization,
        // so it must no longer appear as a standalone pass.
        Assert.DoesNotContain(
            "ProjectJavaPatternRewritePass",
            pipeline.LastPassMetrics.Select(metric => metric.Name));
    }
}
