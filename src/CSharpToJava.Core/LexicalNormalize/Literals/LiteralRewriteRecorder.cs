namespace CSharpToJava.Core.LexicalNormalize.Literals;

/// <summary>
/// Pre-rewrite value/byte snapshots plus per-bucket counters. Feeds the LL-5
/// multiset subset checks, MutableLexicalStats and the per-file aggregated
/// Info diagnostics (design §4.6/§4.7).
/// </summary>
internal sealed class LiteralRewriteRecorder
{
    public int RawSingleLine;
    public int RawMultiLine;
    public int Utf8Expanded;
    public int Utf8Bytes;
    public int VerbatimNormalized;
    public int EscStrings;
    public int EscChars;
    public int InterpolatedRegular;
    public int InterpolatedVerbatim;
    public int InterpolatedRaw;
    public int ResidualCount;

    private readonly List<string> _expectedStringValues = new();
    private readonly List<char> _expectedCharValues = new();
    private readonly List<byte[]> _expectedUtf8Blobs = new();

    public IReadOnlyList<string> ExpectedStringValues => _expectedStringValues;
    public IReadOnlyList<char> ExpectedCharValues => _expectedCharValues;
    public IReadOnlyList<byte[]> ExpectedUtf8Blobs => _expectedUtf8Blobs;

    public int TotalRewrites =>
        RawSingleLine + RawMultiLine + Utf8Expanded + VerbatimNormalized + EscStrings + EscChars;

    public void RecordRaw(bool multiLine, string value)
    {
        if (multiLine)
        {
            RawMultiLine++;
        }
        else
        {
            RawSingleLine++;
        }

        _expectedStringValues.Add(value);
    }

    public void RecordVerbatim(string value)
    {
        VerbatimNormalized++;
        _expectedStringValues.Add(value);
    }

    public void RecordEscString(string value)
    {
        EscStrings++;
        _expectedStringValues.Add(value);
    }

    public void RecordEscChar(char value)
    {
        EscChars++;
        _expectedCharValues.Add(value);
    }

    public void RecordUtf8(byte[] bytes)
    {
        Utf8Expanded++;
        Utf8Bytes += bytes.Length;
        _expectedUtf8Blobs.Add(bytes);
    }

    public void CopyTo(MutableLexicalStats stats)
    {
        stats.RawLiteralsSingleLine += RawSingleLine;
        stats.RawLiteralsMultiLine += RawMultiLine;
        stats.Utf8LiteralsExpanded += Utf8Expanded;
        stats.Utf8BytesEmitted += Utf8Bytes;
        stats.VerbatimLiteralsNormalized += VerbatimNormalized;
        stats.EscEscapesNormalized += EscStrings + EscChars;
        stats.InterpolatedRegularSkipped += InterpolatedRegular;
        stats.InterpolatedVerbatimSkipped += InterpolatedVerbatim;
        stats.InterpolatedRawSkipped += InterpolatedRaw;
        stats.LiteralResidualCount += ResidualCount;
    }

    /// <summary>One aggregated Info diagnostic per non-empty bucket (design §4.7).</summary>
    public List<LexicalNormalizeDiagnostic> BuildAggregatedInfo()
    {
        var result = new List<LexicalNormalizeDiagnostic>();

        if (RawSingleLine + RawMultiLine > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.RawLiteralInfo,
                $"[LL-1] raw string literals normalized: {RawSingleLine + RawMultiLine} " +
                $"({RawMultiLine} multi-line)"));
        }

        if (EscStrings + EscChars > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.EscEscapeInfo,
                $"[LL-6] \\e escapes rewritten: {EscStrings + EscChars} " +
                $"({EscStrings} string(s), {EscChars} char(s))"));
        }

        if (Utf8Expanded > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.Utf8LiteralInfo,
                $"[LL-2] u8 literals expanded: {Utf8Expanded} ({Utf8Bytes} byte(s))"));
        }

        if (VerbatimNormalized > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.VerbatimLiteralInfo,
                $"[LL-3] verbatim string literals normalized: {VerbatimNormalized}"));
        }

        var interpolated = InterpolatedRegular + InterpolatedVerbatim + InterpolatedRaw;
        if (interpolated > 0)
        {
            result.Add(Info(LexicalNormalizeCodes.InterpolatedSkipInfo,
                $"[LL-4] interpolated strings skipped for C23: {interpolated} " +
                $"(regular {InterpolatedRegular}, verbatim {InterpolatedVerbatim}, raw {InterpolatedRaw})"));
        }

        return result;
    }

    private static LexicalNormalizeDiagnostic Info(string code, string message)
        => new(LexicalNormalizeSeverity.Info, code, message);
}
