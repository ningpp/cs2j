using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Xunit;

namespace CSharpToJava.ConversionTests;

/// <summary>
/// Shared infrastructure for conversion-correctness tests.
/// Each test converts a C# snippet via <see cref="ConversionPipeline"/> and verifies
/// the generated Java is correct (conversion succeeded and contains no C#-specific residue).
/// </summary>
public abstract class ConversionTestBase
{
    private static readonly ConversionPipeline Pipeline = new();

    /// <summary>Resolved path to the default type-mapping configuration (copied next to the test assembly).</summary>
    private static readonly string ConfigPath = ResolveConfigPath();

    private static string ResolveConfigPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", "TypeMappings.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "TypeMappings.json"),
            @"d:\code\cs2j\config\TypeMappings.json",
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full))
                return full;
        }
        return candidates[0];
    }

    /// <summary>Converts a C# source snippet to Java and returns the result.</summary>
    protected static ConversionResult Convert(string sourceCode, string fileName = "Test.cs")
    {
        return Pipeline.Convert(new ConversionRequest
        {
            SourceCode = sourceCode,
            FileName = fileName,
            Options = new ConversionOptions { TypeMappingConfigPath = ConfigPath },
        });
    }

    /// <summary>Asserts the conversion succeeded (no error diagnostics, emit succeeded).</summary>
    protected static void AssertSuccess(ConversionResult result, string? context = null)
    {
        Assert.True(result.Success,
            (context == null ? "" : context + "\n") +
            "Conversion failed.\n--- Diagnostics ---\n" +
            string.Join("\n", result.Diagnostics) +
            "\n--- Generated ---\n" + result.GeneratedCode);
    }

    /// <summary>Asserts the generated Java contains the expected substring.</summary>
    protected static void AssertJavaContains(ConversionResult result, string expected, string? because = null)
    {
        Assert.True(result.GeneratedCode.Contains(expected, StringComparison.Ordinal),
            (because ?? $"Expected generated Java to contain '{expected}'.") +
            "\n--- Generated ---\n" + result.GeneratedCode);
    }

    /// <summary>Asserts the generated Java does NOT contain the given substring.</summary>
    protected static void AssertJavaDoesNotContain(ConversionResult result, string unwanted, string? because = null)
    {
        Assert.False(result.GeneratedCode.Contains(unwanted, StringComparison.Ordinal),
            (because ?? $"Expected generated Java NOT to contain '{unwanted}'.") +
            "\n--- Generated ---\n" + result.GeneratedCode);
    }

    /// <summary>
    /// Strong correctness signal: a correct C#-to-Java conversion must not leave C#-specific
    /// syntax in the emitted code. These tokens are never valid Java.
    /// </summary>
    protected static void AssertNoCSharpResidue(ConversionResult result)
    {
        AssertJavaDoesNotContain(result, "=>", "C# lambda/expression-body '=>' must be rewritten to Java '->'");
        AssertJavaDoesNotContain(result, "?.", "C# null-conditional '?.' must be rewritten");
        AssertJavaDoesNotContain(result, "??", "C# null-coalescing '??' must be rewritten");
        AssertJavaDoesNotContain(result, "$\"", "C# interpolated string '$\"' must be rewritten");
        AssertJavaDoesNotContain(result, "nameof(", "C# 'nameof(...)' must be resolved to a literal");
    }

    /// <summary>Runs the full standard correctness check used by most category tests.</summary>
    protected static void AssertConversion(ConversionResult result, params string[] expectedMarkers)
    {
        AssertSuccess(result);
        AssertNoCSharpResidue(result);
        foreach (var marker in expectedMarkers)
            AssertJavaContains(result, marker);
    }

    /// <summary>Asserts the generated Java contains at least one of the supplied markers.</summary>
    protected static void AssertJavaContainsAny(ConversionResult result, params string[] markers)
    {
        foreach (var marker in markers)
        {
            if (result.GeneratedCode.Contains(marker, StringComparison.Ordinal))
                return;
        }
        Assert.True(false,
            "Expected generated Java to contain at least one of: " + string.Join(", ", markers) +
            "\n--- Generated ---\n" + result.GeneratedCode);
    }
}
