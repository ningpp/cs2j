using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// C# enum arithmetic's result type is the enum's UNDERLYING type. For a byte
/// enum, `(int)(None - String)` wraps at byte width: unchecked (byte)(0 - 7) is
/// 249, not -7. Java ordinal() arithmetic stays int, so a comparison like
/// IsTokenTypePrimitive(None) — ((byte)(None - String) <= (byte)(Null - String),
/// i.e. 249 <= 4 in C# (false) vs -7 <= 4 in Java (true) — silently flips.
/// Regression: Utf8JsonReader's comment-after-value handling dispatched to
/// StartArray instead of the previous token and threw 312 JsonReaderExceptions.
/// </summary>
public class ByteEnumArithmeticWrapTests
{
    [Fact]
    public void ByteEnumSubtraction_WrapsAtByteWidth()
    {
        var result = Convert(@"
enum TokenType : byte
{
    None,
    StartObject,
    EndObject,
    StartArray,
    EndArray,
    PropertyName,
    Comment,
    String,
    Number,
    True,
    False,
    Null,
}

class Helper
{
    static bool IsTokenTypePrimitive(TokenType tokenType)
    {
        return (tokenType - TokenType.String) <= (TokenType.Null - TokenType.String);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // The C# byte-wrap semantics must survive: mask the enum subtraction result.
        Assert.Contains("& 0xFF", code, StringComparison.Ordinal);
    }

    [Fact]
    public void IntEnumSubtraction_NoMaskNeeded()
    {
        var result = Convert(@"
enum TokenType
{
    None,
    String,
    Null,
}

class Helper
{
    static bool IsTokenTypePrimitive(TokenType tokenType)
    {
        return (tokenType - TokenType.String) <= (TokenType.Null - TokenType.String);
    }
}");

        Assert.True(result.Success, string.Join("\n", result.Diagnostics) + "\n---Generated---\n" + result.GeneratedCode);
        var code = result.GeneratedCode ?? "";

        // int-backed enums do not wrap — no 0xFF mask may appear.
        Assert.DoesNotContain("0xFF", code, StringComparison.Ordinal);
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
