using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// When a byte[] initializer has >= 512 elements, the converter encodes it as
/// Base64 and emits Base64.getDecoder().decode(...).  If the generated Java file
/// also imports io.github.ningpp.compat.* (which contains its own Base64), the
/// unqualified reference becomes ambiguous.  The converter must use the fully
/// qualified name java.util.Base64 to avoid this.
/// </summary>
public class ByteArrayBase64AmbiguousImportTests
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
}
