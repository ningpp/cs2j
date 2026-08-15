using CSharpToJava.Core.UnsignedTypeRewriter;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

/// <summary>
/// C# byte maps to Java int for scalar values, but byte[] stays Java byte[].
/// The unsigned rewriter therefore must keep an explicit (byte) cast around
/// UnsignedMath.ToByteInt32(...) when the value lands in a byte[] initializer;
/// otherwise javac reports "incompatible types: possible lossy conversion from int to byte".
/// </summary>
public class UnsignedByteArrayInitializerTests
{
    [Fact]
    public void ByteArrayInitializer_CastToByteIsPreservedAroundToByteInt32()
    {
        var source = """
public class C
{
    public static byte[] Values => new byte[] { (byte)'a', (byte)'b' };
}
""";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "unsigned-array-test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = new UnsignedTypeRewriter().Rewrite(source, compilation.GetSemanticModel(tree));

        Assert.True(result.Changed, "The unsigned rewriter should have rewritten the byte casts.");
        Assert.Contains("System.Byte[]", result.OutputCode, StringComparison.Ordinal);
        Assert.Contains("ToByteInt32", result.OutputCode, StringComparison.Ordinal);

        // The value is still emitted into a Java byte[], so the generated C#
        // must contain an explicit (byte) narrowing around the helper call.
        var hasByteCast =
            result.OutputCode.Contains("(byte)Cs2jUnsigned.UnsignedMath.ToByteInt32", StringComparison.Ordinal)
            || result.OutputCode.Contains("(byte) Cs2jUnsigned.UnsignedMath.ToByteInt32", StringComparison.Ordinal);
        Assert.True(hasByteCast, $"Expected (byte) cast before ToByteInt32 in byte[] initializer.\n{result.OutputCode}");
    }

    [Fact]
    public void ByteArrayInitializer_GeneratedJavaKeepsByteCast()
    {
        var result = Convert("""
namespace Cs2jUnsigned
{
    public static class UnsignedMath
    {
        public static byte ToByteInt32(int v) => (byte)v;
    }
}

public class C
{
    public static byte[] Values => new byte[] { (byte)Cs2jUnsigned.UnsignedMath.ToByteInt32('a') };
}
""");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";
        Assert.Contains("new byte[] {", code, StringComparison.Ordinal);

        // The converter must keep the Java (byte) narrowing inside byte[].
        var hasByteCast =
            code.Contains("(byte)(UnsignedMath.toByteInt32", StringComparison.Ordinal)
            || code.Contains("(byte) (UnsignedMath.toByteInt32", StringComparison.Ordinal)
            || code.Contains("(byte)UnsignedMath.toByteInt32", StringComparison.Ordinal)
            || code.Contains("(byte) UnsignedMath.toByteInt32", StringComparison.Ordinal);
        Assert.True(hasByteCast, $"Expected a Java (byte) cast around toByteInt32 in byte[] initializer.\n{code}");
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
