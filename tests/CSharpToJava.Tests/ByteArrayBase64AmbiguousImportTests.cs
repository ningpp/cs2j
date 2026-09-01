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
    [Fact]
    public void LargeByteArrayInitializer_UsesFullyQualifiedBase64()
    {
        // Build a C# source with a byte[] of 512+ elements so the Base64 threshold is hit.
        var bytes = new System.Text.StringBuilder();
        bytes.Append("public static byte[] Data = { ");
        for (int i = 0; i < 520; i++)
        {
            if (i > 0) bytes.Append(", ");
            bytes.Append((i % 256).ToString());
        }
        bytes.Append(" };");

        var source = "namespace TestNs\n" +
                     "{\n" +
                     "    public class TestClass\n" +
                     "    {\n" +
                     "        " + bytes + "\n" +
                     "    }\n" +
                     "}\n";

        var result = Convert(source);

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The converter must emit fully-qualified java.util.Base64 to avoid
        // ambiguity with io.github.ningpp.compat.Base64.
        Assert.Contains("java.util.Base64.getDecoder().decode", code, StringComparison.Ordinal);

        // It must NOT emit an unqualified Base64.getDecoder() call (i.e. without
        // the java.util. prefix).  Check by ensuring no occurrence of Base64
        // is preceded by something other than "java.util.".
        // Simple approach: replace all "java.util.Base64" with a placeholder,
        // then verify no "Base64.getDecoder" remains.
        var sanitized = code.Replace("java.util.Base64", "__FQ__");
        Assert.DoesNotContain("Base64.getDecoder", sanitized, StringComparison.Ordinal);
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
