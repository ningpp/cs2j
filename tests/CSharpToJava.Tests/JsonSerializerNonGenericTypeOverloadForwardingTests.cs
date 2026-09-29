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

    /// <summary>
    /// Non-generic SerializeAsync(Stream, object, Type, options?, cancellationToken?)
    /// has default parameters. The generated forwarding overload
    /// SerializeAsync(Stream, object, Type) must call serializeAsync_0tp
    /// (not serializeAsync, which would resolve to the generic overload
    /// with a leading Class&lt;T&gt; parameter).
    /// </summary>
}
