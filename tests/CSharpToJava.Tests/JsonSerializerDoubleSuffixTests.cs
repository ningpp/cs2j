using System.Linq;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the serialize_0tp_0tp double-suffix bug.
///
/// When a non-generic JsonSerializer.Serialize overload (taking System.Type)
/// coexists with a private generic Serialize&lt;TValue&gt; that has the same
/// parameter count, GetErasureConflictSuffix returns _0tp (same as the
/// hardcoded JsonSerializer suffix). If both are applied, the result is
/// serialize_0tp_0tp — a method that doesn't exist.
///
/// The fix mirrors MethodTransformer's guard: skip GetErasureConflictSuffix
/// when the method name already ends with _0tp.
/// </summary>
public class JsonSerializerDoubleSuffixTests
{
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

    /// <summary>
    /// A member-access call to JsonSerializer.Serialize(writer, input, type, options)
    /// must produce serialize_0tp (not serialize_0tp_0tp) when a private generic
    /// Serialize&lt;TValue&gt;(writer, in TValue, Type, options) with the same param
    /// count exists — GetErasureConflictSuffix returns _0tp for the same conflict
    /// that the hardcoded logic already covers.
    /// </summary>
    [Fact]
    public void MemberAccess_NonGenericSerialize_WithPrivateGenericSameParamCount_NoDoubleSuffix()
    {
        var result = Convert("""
            using System;
            using System.IO;

            namespace System.Text.Json
            {
                public static class JsonSerializer
                {
                    // Public generic with default params (3 C# params)
                    public static void Serialize<TValue>(
                        Utf8JsonWriter writer, TValue value,
                        JsonSerializerOptions options = null)
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                    }

                    // Public non-generic (4 C# params, takes System.Type)
                    public static void Serialize(
                        Utf8JsonWriter writer, object value, Type inputType,
                        JsonSerializerOptions options = null)
                    {
                        Serialize<object>(writer, value, inputType, options);
                    }

                    // Private generic with same param count (4 C# params) — causes
                    // GetErasureConflictSuffix to also return _0tp for the non-generic
                    // overload, which would produce serialize_0tp_0tp without the guard.
                    private static void Serialize<TValue>(
                        Utf8JsonWriter writer, in TValue value, Type type,
                        JsonSerializerOptions options)
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                    }
                }

                public class JsonSerializerOptions { }
                public class Utf8JsonWriter { }
            }

            public class Test
            {
                public void M(System.Text.Json.Utf8JsonWriter writer,
                    object input, Type type, System.Text.Json.JsonSerializerOptions options)
                {
                    System.Text.Json.JsonSerializer.Serialize(writer, input, type, options);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Must NOT contain serialize_0tp_0tp in code (only in comments is OK)
        var codeLines = code.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//") && !l.StartsWith("*") && !l.StartsWith("/*"))
            .ToList();
        var doubleSuffixLines = codeLines
            .Where(l => l.Contains("_0tp_0tp"))
            .ToList();
        Assert.True(doubleSuffixLines.Count == 0,
            $"Found _0tp_0tp in code lines:\n{string.Join("\n", doubleSuffixLines)}");
        // Should contain serialize_0tp (single suffix) at the call site
        Assert.Contains("serialize_0tp", code);
    }

    /// <summary>
    /// Declarations must also not get a double suffix.
    /// </summary>
    [Fact]
    public void Declaration_NonGenericSerialize_WithPrivateGenericSameParamCount_NoDoubleSuffix()
    {
        var result = Convert("""
            using System;

            namespace System.Text.Json
            {
                public static class JsonSerializer
                {
                    public static void Serialize<TValue>(
                        object writer, TValue value,
                        JsonSerializerOptions options = null)
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                    }

                    public static void Serialize(
                        object writer, object value, Type inputType,
                        JsonSerializerOptions options = null)
                    {
                    }

                    private static void Serialize<TValue>(
                        object writer, in TValue value, Type type,
                        JsonSerializerOptions options)
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                    }
                }

                public class JsonSerializerOptions { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        var codeLines2 = code.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//") && !l.StartsWith("*") && !l.StartsWith("/*"))
            .ToList();
        var doubleSuffixLines2 = codeLines2
            .Where(l => l.Contains("_0tp_0tp"))
            .ToList();
        Assert.True(doubleSuffixLines2.Count == 0,
            $"Found _0tp_0tp in code lines:\n{string.Join("\n", doubleSuffixLines2)}");
    }
}
