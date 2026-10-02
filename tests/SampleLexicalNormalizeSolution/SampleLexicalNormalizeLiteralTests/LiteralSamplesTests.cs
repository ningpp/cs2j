using System.Text;
using SampleLexicalNormalizeLib;
using Xunit;

namespace SampleLexicalNormalizeLiteralTests;

/// <summary>
/// Runtime (half-side) value assertions. They run against the original
/// fixture AND the normalized intermediate tree, so they are the .NET-side
/// proof that raw indentation, u8 bytes, verbatim content and \e survive the
/// lexical pass unchanged in value (design §7.2).
/// </summary>
public sealed class LiteralSamplesTests
{
    [Fact]
    public void RawSingleLine_HasExpectedValue()
        => Assert.Equal("hello", LiteralSamples.Single);

    [Fact]
    public void RawMultiLine_StripsIndentation_ExcludesClosingNewline()
        => Assert.Equal("line1\nline2", LiteralSamples.Multi);

    [Fact]
    public void Utf8_MatchesUtf8EncodingOfValue()
        => Assert.Equal(Encoding.UTF8.GetBytes("hello中"), LiteralSamples.GreetingBytes());

    [Fact]
    public void Verbatim_KeepsSqlContent()
    {
        // verbatim 原样保留源换行：fixture 若以 CRLF 落盘，值里就是 \r\n，先归一再断言。
        var sql = LiteralSamples.Sql.Replace("\r\n", "\n");
        Assert.StartsWith("\nSELECT *\n", sql);
        Assert.EndsWith("WHERE id = 1", sql);
    }

    [Fact]
    public void EscEscapes_DecodeToEscapeCharacter()
    {
        Assert.Equal("a\u001Bb", LiteralSamples.EscString);
        Assert.Equal('\u001B', LiteralSamples.EscChar);
    }

    [Fact]
    public void InterpolatedForms_KeepRuntimeValues()
    {
        Assert.Equal("raw 7", LiteralSamples.InterpolatedRaw(7));
        Assert.Equal("7", LiteralSamples.InterpolatedDoubleDollar(7));
        Assert.Equal("a\"7", LiteralSamples.InterpolatedVerbatim(7));
    }
}
