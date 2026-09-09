using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression tests for the _0tp suffix not being applied to
/// default-parameter forwarding overloads of non-generic
/// JsonSerializer.DeserializeAsync/SerializeAsync methods.
///
/// When a non-generic overload (taking System.Type) is split into
/// multiple Java overloads by DefaultParameterOverloadBuilder, the
/// forwarding body must call deserializeAsync_0tp / serializeAsync_0tp
/// (not the generic deserializeAsync / serializeAsync), otherwise
/// Java's overload resolution fails due to parameter order mismatch
/// (the generic version has Class&lt;T&gt; in a different position).
/// </summary>
public class JsonSerializerNonGenericTypeOverloadForwardingTests
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
    /// Non-generic DeserializeAsync(Stream, Type, options?, cancellationToken?)
    /// has default parameters. The generated forwarding overload
    /// DeserializeAsync(Stream, Type) must call deserializeAsync_0tp
    /// (not deserializeAsync, which would resolve to the generic overload
    /// with a different Class&lt;?&gt; parameter position).
    /// </summary>
    [Fact]
    public void NonGenericDeserializeAsync_DefaultParamOverload_Calls0tpNotGeneric()
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
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                        return default;
                    }

                    public static ValueTask<object> DeserializeAsync(
                        Stream utf8Json,
                        Type returnType,
                        JsonSerializerOptions options = null,
                        CancellationToken cancellationToken = default)
                    {
                        return DeserializeAsync<object>(utf8Json, options, cancellationToken);
                    }
                }

                public class JsonSerializerOptions { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The non-generic method should be declared as deserializeAsync_0tp
        Assert.Contains("deserializeAsync_0tp", code);

        // The forwarding overload (fewer args) must call deserializeAsync_0tp,
        // NOT bare deserializeAsync (which would resolve to the generic version
        // with a different Class<?> parameter order).
        // The forwarding overload has signature (Stream, Type) and calls
        // the full method with (stream, type, null, default). This call
        // must use deserializeAsync_0tp, not deserializeAsync.
        // We check that there's a call to deserializeAsync_0tp that does NOT
        // start with _cs2j_TValue (which would be the generic version's call).
        var lines = code.Split('\n');
        bool found0tpForwardingCall = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Look for the forwarding call: deserializeAsync_0tp(utf8Json, ...
            // (not the generic version which starts with _cs2j_TValue)
            if (trimmed.Contains("deserializeAsync_0tp(") && !trimmed.Contains("_cs2j_"))
            {
                found0tpForwardingCall = true;
                break;
            }
        }
        Assert.True(found0tpForwardingCall,
            "Expected a forwarding call to deserializeAsync_0tp (without _cs2j_ prefix) " +
            "but none was found.\nGenerated code:\n" + code);
    }

    /// <summary>
    /// Non-generic SerializeAsync(Stream, object, Type, options?, cancellationToken?)
    /// has default parameters. The generated forwarding overload
    /// SerializeAsync(Stream, object, Type) must call serializeAsync_0tp
    /// (not serializeAsync, which would resolve to the generic overload
    /// with a leading Class&lt;T&gt; parameter).
    /// </summary>
    [Fact]
    public void NonGenericSerializeAsync_DefaultParamOverload_Calls0tpNotGeneric()
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
                    public static Task SerializeAsync<TValue>(
                        Stream utf8Json,
                        TValue value,
                        JsonSerializerOptions options = null,
                        CancellationToken cancellationToken = default)
                        where TValue : Exception
                    {
                        var _ = typeof(TValue);
                        return Task.FromResult<object>(null);
                    }

                    public static Task SerializeAsync(
                        Stream utf8Json,
                        object value,
                        Type inputType,
                        JsonSerializerOptions options = null,
                        CancellationToken cancellationToken = default)
                    {
                        return SerializeAsync<object>(utf8Json, value, options, cancellationToken);
                    }
                }

                public class JsonSerializerOptions { }
            }
            """);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var code = result.GeneratedCode ?? "";

        // The non-generic method should be declared as serializeAsync_0tp
        Assert.Contains("serializeAsync_0tp", code);

        // The forwarding overload (fewer args) must call serializeAsync_0tp,
        // NOT bare serializeAsync (which would resolve to the generic version
        // with a leading Class<T> parameter).
        var lines = code.Split('\n');
        bool found0tpForwardingCall = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Look for the forwarding call: serializeAsync_0tp(utf8Json, ...
            // (not the generic version which starts with _cs2j_TValue)
            if (trimmed.Contains("serializeAsync_0tp(") && !trimmed.Contains("_cs2j_"))
            {
                found0tpForwardingCall = true;
                break;
            }
        }
        Assert.True(found0tpForwardingCall,
            "Expected a forwarding call to serializeAsync_0tp (without _cs2j_ prefix) " +
            "but none was found.\nGenerated code:\n" + code);
    }
}
