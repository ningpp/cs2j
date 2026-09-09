using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for Class token placement when calling generic methods
/// whose Java signature has a TRAILING Class&lt;?&gt; parameter (added by
/// AddRuntimeClassParametersForTypeParameterArrays).
///
/// In single-file conversion mode, generic methods that use typeof(T) get
/// a LEADING Class&lt;T&gt; _cs2j_T parameter (via AddPendingRuntimeClassParameters).
/// Call sites must PREPEND the Class literal to match.
///
/// In project conversion mode, the same methods may get a TRAILING Class&lt;?&gt;
/// parameter (via AddRuntimeClassParametersForTypeParameterArrays, because
/// GetRequiredTypeParameters detects the requirement via forwarded calls).
/// The MethodHasTrailingClassParams check must correctly identify trailing
/// usage even when GetClassTypeTokensForCall has registered leading params
/// (a false positive from the call-site scan).
/// </summary>
public class DeserializeAsyncClassTokenPlacementTests
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
    /// In single-file mode, generic methods with typeof(T) use LEADING Class<T>
    /// parameters. Call sites must PREPEND the Class literal. This test verifies
    /// that the fix doesn't break this behavior.
    /// </summary>
    [Fact]
    public void GenericMethodWithLeadingClass_CallSite_PrependsClassToken()
    {
        var result = Convert("""
            using System;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;

            namespace System.Text.Json
            {
                public static class JsonSerializer
                {
                    public static ValueTask<TValue> DeserializeAsync<TValue>(
                        Stream utf8Json,
                        JsonSerializerOptions options = null,
                        CancellationToken cancellationToken = default)
                    {
                        return ReadAsync<TValue>(utf8Json, typeof(TValue), options, cancellationToken);
                    }

                    private static ValueTask<TValue> ReadAsync<TValue>(
                        Stream utf8Json,
                        Type returnType,
                        JsonSerializerOptions options,
                        CancellationToken cancellationToken)
                    {
                        var t = typeof(TValue);
                        return default;
                    }
                }

                public class JsonSerializerOptions { }
            }

            public class Test
            {
                public void M()
                {
                    var ms = new System.IO.MemoryStream();
                    var result = System.Text.Json.JsonSerializer.DeserializeAsync<Test>(ms);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // Find the call site line containing deserializeAsync and Test.class
        var lines = code.Split('\n');
        var callLine = lines.FirstOrDefault(l =>
            l.Contains("deserializeAsync(") && l.Contains("Test.class"));
        Assert.NotNull(callLine);

        // In single-file mode, leading Class<T> is used, so Test.class comes FIRST
        var classPos = callLine.IndexOf("Test.class");
        var streamPos = callLine.IndexOf("ms");
        Assert.True(streamPos >= 0 && classPos >= 0,
            $"Expected both 'ms' and 'Test.class' in call line: {callLine}");
        Assert.True(classPos < streamPos,
            $"Expected 'Test.class' BEFORE 'ms' (leading position) in call line: {callLine}");
    }
}
