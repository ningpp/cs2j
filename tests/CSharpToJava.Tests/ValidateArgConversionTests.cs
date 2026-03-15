using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Validates that C# PascalCase static utility methods (e.g. ValidateArg.IsNotNull)
/// are correctly lowercased to camelCase at both the declaration site and every call site.
/// The companion fix lives in InvocationExpressionTransformer.TransformMemberInvocation.
/// </summary>
public class ValidateArgConversionTests
{
    private static ConversionResult Convert(string csharpCode)
    {
        var pipeline = new ConversionPipeline();
        return pipeline.Convert(new ConversionRequest { SourceCode = csharpCode });
    }

    // ── Test 1 ─────────────────────────────────────────────────────────────────
    // The ValidateArg class declaration should become:
    //   • public final class ValidateArg  (static class → final + private ctor)
    //   • static void isNotNull            (PascalCase → camelCase on declaration)
    //   • throw new NullPointerException   (ArgumentNullException → NullPointerException)

    [Fact]
    public void ValidateArg_Declaration_ConvertsCorrectly()
    {
        const string code = """
            using System;

            internal static class ValidateArg
            {
                public static void IsNotNull(object arg, string parameterName)
                {
                    if (arg == null)
                    {
                        throw new ArgumentNullException(parameterName);
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // static class becomes final with a private no-arg constructor
        Assert.Contains("final class ValidateArg", result.GeneratedCode);
        // Method name must be lowercased
        Assert.Contains("isNotNull", result.GeneratedCode);
        // ArgumentNullException → NullPointerException
        Assert.Contains("NullPointerException", result.GeneratedCode);
    }

    // ── Test 2 ─────────────────────────────────────────────────────────────────
    // At every call site ValidateArg.IsNotNull(…) must become
    // ValidateArg.isNotNull(…)  — lowercase first letter, matching the declaration.

    [Fact]
    public void ValidateArg_CallSite_ConvertsToLowerCamelCase()
    {
        const string code = """
            using System;

            internal static class ValidateArg
            {
                public static void IsNotNull(object arg, string parameterName)
                {
                    if (arg == null)
                    {
                        throw new ArgumentNullException(parameterName);
                    }
                }
            }

            internal class DemoCaller
            {
                object[] elements;

                public void CheckElements()
                {
                    ValidateArg.IsNotNull(elements, "elements");
                    foreach (var element in elements)
                    {
                        ValidateArg.IsNotNull(element, "element");
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");

        // Both call sites must use lower-camel form
        Assert.Contains("ValidateArg.isNotNull(elements,", result.GeneratedCode);
        Assert.Contains("ValidateArg.isNotNull(element,", result.GeneratedCode);
        // Upper-case form must NOT appear at call sites
        Assert.DoesNotContain("ValidateArg.IsNotNull(", result.GeneratedCode);
    }

    // ── Test 3 ─────────────────────────────────────────────────────────────────
    // The foreach loop over an object array must become a Java enhanced-for loop.

    [Fact]
    public void ValidateArg_Foreach_ConvertsToEnhancedForLoop()
    {
        const string code = """
            internal class DemoCaller
            {
                object[] elements;

                public void CheckElements()
                {
                    foreach (var element in elements)
                    {
                        _ = element;
                    }
                }
            }
            """;

        var result = Convert(code);

        Assert.True(result.Success,
            $"Conversion failed:\n{string.Join("\n", result.Diagnostics.Select(d => d.Message))}");
        // C# foreach over array → Java enhanced-for
        Assert.Contains("for (Object element : elements)", result.GeneratedCode);
    }
}
