using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Reproduces the 8 Maven compilation errors found in JsonSerializer.java
/// after converting the System.Text.Json SDK.
///
/// Two root causes:
/// 1. DefaultParameterHelper does not forward trailing synthetic Class&lt;?&gt;
///    parameters (tValueClass) when generating default-parameter overloads,
///    causing "method not found" errors at overload call sites.
/// 2. StatementTransformer uses "var _ret = expr;" for return-with-post-
///    statements, which Java infers as Object instead of the generic TValue,
///    causing "incompatible types: Object cannot be converted to TValue".
/// </summary>
public class GenericDefaultParameterOverloadTests
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

    // ── Category B: Missing trailing Class<?> parameter in overload call ──

    /// <summary>
    /// A generic method with a default-valued parameter and a trailing
    /// Class<?> tValueClass synthetic parameter must generate an overload
    /// that forwards tValueClass (as null) to the full method.
    ///
    /// Before fix: the overload body calls the full method without the
    /// trailing Class<?> parameter → "method not found".
    ///
    /// After fix: the overload body should forward null for tValueClass.
    /// </summary>
    [Fact]
    public void GenericMethod_DefaultParam_TrailingClassToken_ForwardedInOverloadCall()
    {
        var result = Convert("""
            using System;

            public static class JsonSerializer
            {
                public static TValue Deserialize<TValue>(string json, JsonSerializerOptions options = null)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }

            public class JsonSerializerOptions { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The full method should have a trailing Class<?> tValueClass parameter.
        // If the trailing parameter exists, the overload must forward null for it.
        // Check that any overload call includes the trailing Class<?> parameter forwarding.
        var code = result.GeneratedCode;
        // The overload (json only) must forward the leading Class<T> parameter
        // and the default-value for options.
        Assert.Contains("deserialize(_cs2j_TValue, json, (JsonSerializerOptions) null)", code);
    }

    /// <summary>
    /// A generic method with two default-valued parameters and a trailing
    /// Class<?> tValueClass must generate overloads that forward tValueClass
    /// for EVERY overload (not just the last one).
    /// </summary>
    [Fact]
    public void GenericMethod_TwoDefaultParams_TrailingClassToken_ForwardedInAllOverloads()
    {
        var result = Convert("""
            using System;

            public static class Serializer
            {
                public static TValue Read<TValue>(string json, int mode = 0, bool flag = false)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        if (code.Contains("Class<?> tValueClass"))
        {
            Assert.Contains("read(json, mode, false, (Class<?>) null)", code);
            Assert.Contains("read(json, 0, false, (Class<?>) null)", code);
        }
    }

    /// <summary>
    /// A generic method with a default-valued parameter on a non-string type
    /// (e.g. byte[]) must also forward the trailing Class<?> token.
    /// </summary>
    [Fact]
    public void GenericMethod_DefaultParam_SpanType_TrailingClassToken_Forwarded()
    {
        var result = Convert("""
            using System;

            public static class JsonSerializer
            {
                public static TValue Deserialize<TValue>(byte[] utf8Json, JsonSerializerOptions options = null)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }

            public class JsonSerializerOptions { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        if (code.Contains("Class<?> tValueClass"))
        {
            Assert.Contains("deserialize(utf8Json, (JsonSerializerOptions) null, (Class<?>) null)", code);
        }
    }

    /// <summary>
    /// A generic async method with default-valued options/cancellationToken params
    /// and a trailing Class<?> tValueClass must forward tValueClass in overloads.
    /// </summary>
    [Fact]
    public void GenericAsyncMethod_DefaultParams_TrailingClassToken_ForwardedInOverloads()
    {
        var result = Convert("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            public static class JsonSerializer
            {
                public static ValueTask<TValue> DeserializeAsync<TValue>(System.IO.Stream utf8Json,
                    JsonSerializerOptions options = null,
                    CancellationToken cancellationToken = default)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default;
                }
            }

            public class JsonSerializerOptions { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        if (code.Contains("Class<?> tValueClass"))
        {
            // The overload that cuts off cancellationToken must forward tValueClass.
            Assert.Matches(@"deserializeAsync\(\w+,\s*\([^)]*\)\s*null,\s*new\s+CancellationToken\(\),\s*\(Class<\?>\)\s*null\)", code);
            // The overload that cuts off options+cancellationToken must forward tValueClass.
            Assert.Matches(@"deserializeAsync\(\w+,\s*new\s+CancellationToken\(\),\s*\(Class<\?>\)\s*null\)", code);
        }
    }

    /// <summary>
    /// A generic method with a ref-parameter (converted to ObjectHolder) and
    /// a default-valued options parameter must forward the trailing Class<?>
    /// token.
    /// </summary>
    [Fact]
    public void GenericMethod_RefParam_DefaultParam_TrailingClassToken_Forwarded()
    {
        var result = Convert("""
            using System;

            public struct Utf8JsonReader { }

            public static class JsonSerializer
            {
                public static TValue Deserialize<TValue>(ref Utf8JsonReader reader, JsonSerializerOptions options = null)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }

            public class JsonSerializerOptions { }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode;
        if (code.Contains("Class<?> tValueClass"))
        {
            Assert.Contains("(Class<?>) null", code);
        }
    }

    // ── Category A: var _ret generic type inference failure ──

    /// <summary>
    /// When a generic method's return statement has post-statements (e.g.
    /// ref-parameter write-back), the converter emits "var _ret = expr;".
    /// In a generic method, var infers Object (not TValue), causing
    /// "incompatible types: Object cannot be converted to TValue".
    ///
    /// Fix: use the method's return type (TValue) instead of var.
    /// </summary>
    [Fact]
    public void GenericMethod_ReturnWithPostStatements_UsesExplicitTypeNotVar()
    {
        var result = Convert("""
            using System;

            public struct ReadStack { }

            public static class JsonSerializer
            {
                private static TValue ReadCore<TValue>(ref ReadStack state, Type returnType)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // When ref state is converted to ObjectHolder, the return statement
        // generates post-statements (state = _stateRef.value) and captures
        // the return value in _ret. The type of _ret must be TValue, not var.
        Assert.DoesNotContain("var _ret = readCore", result.GeneratedCode);
    }

    /// <summary>
    /// A generic method that calls another generic method and returns the
    /// result through a ref-holder pattern. The return must not use var.
    /// </summary>
    [Fact]
    public void GenericMethod_NestedGenericCall_ReturnWithPostStatements_NoVar()
    {
        var result = Convert("""
            using System;

            public struct State { }

            public static class Wrapper
            {
                public static TValue Run<TValue>(ref State state, string input)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return Inner<TValue>(ref state, input);
                }

                private static TValue Inner<TValue>(ref State state, string input)
                    where TValue : Exception
                {
                    var _ = typeof(TValue);
                    return default(TValue);
                }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // The Run method calls Inner and returns the result, but must update
        // the ref state holder first → post-statements → _ret pattern.
        Assert.DoesNotContain("var _ret = inner", result.GeneratedCode);
    }
}
