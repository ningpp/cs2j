using CSharpToJava.Core.LexicalNormalize;
using CSharpToJava.Core.LexicalNormalize.Literals;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// C43 Core matrix (M1 slice): LL-1 raw single/multi-line/indentation/newline
/// contract, LL-6 \e scope, LL-5 fidelity rollback, trivia, option gates and
/// the two-direction C28 token-sequence safety-net regression (design §7.1).
/// </summary>
public sealed class LexicalLiteralNormalizeTests
{
    private static readonly CSharpParseOptions ParseOptions =
        CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

    private static LexicalNormalizeResult Run(string source, LexicalNormalizeOptions? options = null)
        => new LexicalNormalizePreprocessor().Normalize(source, options);

    private static LiteralExpressionSyntax ParseLiteralExpression(string literalText)
    {
        var root = CSharpSyntaxTree.ParseText(
            "public class C { object x = " + literalText + "; }", ParseOptions)
            .GetCompilationUnitRoot();
        return root.DescendantNodes().OfType<LiteralExpressionSyntax>().First();
    }

    [Fact]
    public void LL1_RawSingleLine_QuotesBackslashesAndLongerDelimiters_AreRegularStrings()
    {
        // 外层定界符必须是 5 引号：Q 的源码里出现连续 4 引号，4 引号定界会被提前终结。
        const string source = """""
            public class C
            {
                public string A = """hello""";
                public string B = """a"b""";
                public string P = """C:\temp\a""";
                public string Q = """"a"""b"""";
            }
            """"";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(4, result.Statistics.RawLiteralsSingleLine);
        Assert.DoesNotContain("\"\"\"", result.OutputCode);
        Assert.Contains("\"hello\"", result.OutputCode);
        Assert.Contains("\"a\\\"b\"", result.OutputCode);
        Assert.Contains("\"C:\\\\temp\\\\a\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_ZeroColumnClosing_TrimsNothing()
    {
        var source = "public class C\n{\n    public string Z = \"\"\"\na\n\"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_TwoColumnClosing_DeepLineKeepsExtraIndent()
    {
        var source = "public class C\n{\n    public string T = \"\"\"\n  x\n    y\n  \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"x\\n  y\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_FourColumnClosing_TrimsCommonPrefix()
    {
        var source = "public class C\n{\n    public string F = \"\"\"\n    p\n    \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"p\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_TabColumnClosing_TrimsTabPrefix()
    {
        var source = "public class C\n{\n\tpublic string T = \"\"\"\n\tt\n\t\"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"t\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawMultiLine_BlankLine_IsExemptFromIndentationPrefix()
    {
        var source = "public class C\n{\n    public string B = \"\"\"\n    a\n\n    b\n    \"\"\";\n}\n";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\\n\\nb\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    [InlineData("\r")]
    public void LL1_RawMultiLine_AllSourceNewlines_NormalizeToValueLf(string newline)
    {
        var source = "public class C { public string X = "
            + "\"\"\"" + newline + "  a" + newline + "  " + "\"\"\"" + "; }";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.RawLiteralsMultiLine);
        Assert.Contains("\"a\"", result.OutputCode);
        Assert.DoesNotContain("a\\r", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL1_RawIndentationViolation_IsParseError_FilePassesThroughUnchanged()
    {
        // Closing quotes at column 4 while the content line only has 2 spaces.
        var source = "public class C { public string X = "
            + "\"\"\"\n  a\n    \"\"\"" + "; }\n";

        var result = Run(source);

        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Severity == LexicalNormalizeSeverity.Error);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void LL6_BareEsc_InStringAndChar_BecomesUnicodeEscape_OtherFormsUntouched()
    {
        const string source = """"
            public class C
            {
                public string A = "a\eb";
                public char B = '\e';
                public string C = "\\e";
                public string D = @"\e";
                public string E = """\e""";
                public string F(int y) => $"x\e{y}";
            }
            """";

        // 只开 LL-6：raw/verbatim 显式关掉（verbatim 在 M2 才落地，提前关掉可保
        // C/D/E/F「未被本规则触碰」的断言在 M1 与 M2 两个里程碑下都成立）。
        var result = Run(source, new LexicalNormalizeOptions
        {
            NormalizeRawLiterals = false,
            NormalizeUtf8Literals = false,
            NormalizeVerbatimLiterals = false,
        });

        Assert.True(result.Changed);
        Assert.Contains("\"a\\u001bb\"", result.OutputCode);
        Assert.Contains("'\\u001b'", result.OutputCode);
        Assert.Equal(2, result.Statistics.EscEscapesNormalized);
        Assert.Contains("\"\\\\e\"", result.OutputCode);
        Assert.Contains("@\"\\e\"", result.OutputCode);
        Assert.Contains("\"\"\"\\e\"\"\"", result.OutputCode);
        Assert.Contains("$\"x\\e{y}\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL5_MissingExpectedStringValue_FailsFidelity_With5580()
    {
        var recorder = new LiteralRewriteRecorder();
        recorder.RecordRaw(multiLine: false, "missing-value");

        var outcome = LiteralNormalizationVerifier.Verify(
            "public class C { }",
            new LexicalNormalizeOptions(),
            recorder);

        Assert.True(outcome.Fatal);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "CS2J5580");
    }

    [Fact]
    public void LL_RewrittenLiteral_PreservesLeadingAndTrailingTrivia()
    {
        const string source = """"
            // leading comment
            public class C
            { // inner
                public string A = """x"""; // tail
            }
            """";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("// leading comment", result.OutputCode);
        Assert.Contains("// inner", result.OutputCode);
        Assert.Contains("// tail", result.OutputCode);
        Assert.Contains("public string A = \"x\";", result.OutputCode);
    }

    [Fact]
    public void LL1_LL6_DisableSwitches_LeaveFormsUntouched()
    {
        const string source = "public class C { public string A = \"\"\"x\"\"\"; public string B = \"a\\eb\"; }\n";

        var off = Run(source, new LexicalNormalizeOptions
        {
            NormalizeRawLiterals = false,
            NormalizeEscEscapes = false,
        });
        Assert.False(off.Changed);
        Assert.Equal(source, off.OutputCode);

        var rawOnly = Run(source, new LexicalNormalizeOptions { NormalizeEscEscapes = false });
        Assert.True(rawOnly.Changed);
        Assert.Equal(1, rawOnly.Statistics.RawLiteralsSingleLine);
        Assert.Equal(0, rawOnly.Statistics.EscEscapesNormalized);
        Assert.Contains("\"x\"", rawOnly.OutputCode);
        Assert.Contains("\"a\\eb\"", rawOnly.OutputCode);

        var escOnly = Run(source, new LexicalNormalizeOptions { NormalizeRawLiterals = false });
        Assert.True(escOnly.Changed);
        Assert.Equal(0, escOnly.Statistics.RawLiteralsSingleLine);
        Assert.Equal(1, escOnly.Statistics.EscEscapesNormalized);
        Assert.Contains("\"\"\"x\"\"\"", escOnly.OutputCode);
        Assert.Contains("\"a\\u001bb\"", escOnly.OutputCode);
    }

    [Fact]
    public void LN1_TokenSequenceSafetyNet_StillRuns_WhenNoLiteralRewriteOccurred()
    {
        var original = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            "public class C { int a; }", ParseOptions).GetRoot();
        var output = "public class C { int b; }";

        var (passes, failure) =
            LexicalNormalizeVerifier_HasTokenSequenceCoverage(output, original);

        Assert.False(passes);
        Assert.NotNull(failure);
        Assert.Equal(LexicalNormalizeCodes.StructuralCheck, failure!.Code);
    }

    [Fact]
    public void LN1_TokenSequenceSafetyNet_IsRelaxed_WhenLiteralRewriteOccurred()
    {
        var original = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(
            "public class C { int a; }", ParseOptions).GetRoot();
        var output = "public class C { int b; }";

        var (passes, failure) =
            LexicalNormalizeVerifier_HasTokenSequenceCoverage(output, original, literalRewrote: true);

        Assert.True(passes);
        Assert.Null(failure);
    }

    private static (bool Passes, LexicalNormalizeDiagnostic? Failure)
        LexicalNormalizeVerifier_HasTokenSequenceCoverage(
            string output,
            CompilationUnitSyntax original,
            bool literalRewrote = false)
    {
        var passes = LexicalNormalizeVerifier.Verify(
            output,
            original,
            new LexicalNormalizeOptions { NormalizeFileScopedNamespaces = false },
            literalRewrote,
            out var failure);

        return (passes, failure);
    }

    [Fact]
    public void LL2_Utf8_AsciiAndEmpty_EmitHexCasts_WithExactByteCounts()
    {
        const string source = """
            public class C
            {
                public void M()
                {
                    System.ReadOnlySpan<byte> a = "abc"u8;
                    System.ReadOnlySpan<byte> e = ""u8;
                    System.Console.WriteLine(a.Length + e.Length);
                }
            }
            """;

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(2, result.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(3, result.Statistics.Utf8BytesEmitted);
        Assert.Contains("(byte)0x61", result.OutputCode);
        Assert.Contains("(byte)0x62", result.OutputCode);
        Assert.Contains("(byte)0x63", result.OutputCode);
        Assert.Contains("new byte[]", result.OutputCode);
        Assert.DoesNotContain("\"u8", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL2_ByteLiteralFactory_EmitsExpectedCastSyntax()
    {
        var array = ByteLiteralFactory.Create(new byte[] { 0x61, 0xFF });

        // Regression pin (2026-10-02 probe): the convenience factory's
        // auto-supplied `new` keyword serializes without a separator space
        // (`newbyte[]`), which reparses as a syntax error.
        Assert.StartsWith("new byte[]", array.ToFullString());
        Assert.Equal(2, array.Initializer!.Expressions.Count);
        Assert.Equal("(byte)0x61", array.Initializer.Expressions[0].ToString());
        Assert.Equal("(byte)0xFF", array.Initializer.Expressions[1].ToString());
        Assert.Empty(ByteLiteralFactory.Create(System.Array.Empty<byte>()).Initializer!.Expressions);
    }

    [Fact]
    public void LL2_Utf8_ChineseAndEmoji_MatchEncoding_AndEmitNoBom()
    {
        var chinese = ParseLiteralExpression("\"中\"u8");
        var emoji = ParseLiteralExpression("\"😀\"u8");
        var recorder = new LiteralRewriteRecorder();

        var chineseArray = Utf8SuffixLiteralRewriter.TryRewrite(chinese, recorder)!;
        var emojiArray = Utf8SuffixLiteralRewriter.TryRewrite(emoji, recorder)!;

        Assert.Equal(new byte[] { 0xE4, 0xB8, 0xAD }, recorder.ExpectedUtf8Blobs[0]);
        Assert.Equal(new byte[] { 0xF0, 0x9F, 0x98, 0x80 }, recorder.ExpectedUtf8Blobs[1]);
        Assert.Equal(2, recorder.Utf8Expanded);
        Assert.Equal(7, recorder.Utf8Bytes);
        Assert.DoesNotContain("0xEF", chineseArray.ToString());
        Assert.DoesNotContain("0xEF", emojiArray.ToString());
    }

    [Fact]
    public void LL2_Utf8_HalfRangeBytes_7F_80_FF_ArePreserved()
    {
        var node = ParseLiteralExpression("\"\u007F\u0080\u00FF\"u8");
        var recorder = new LiteralRewriteRecorder();

        Utf8SuffixLiteralRewriter.TryRewrite(node, recorder);

        Assert.Equal(new byte[] { 0x7F, 0xC2, 0x80, 0xC3, 0xBF }, recorder.ExpectedUtf8Blobs[0]);
    }

    [Fact]
    public void LL2_Utf8_LoneSurrogate_NetReplacement_EmitsFffdBlob()
    {
        var source = "public class C { object x = \"\uD800\"u8; }\n";

        // M0 probe follow-up (2026-10-02): CS9026 is a compilation-time
        // diagnostic that this pure-syntax pass (no Compilation) never sees —
        // the syntax tree parses clean, so the D5 branch-B mapping "parse
        // errors roll the file back" does not exist. The pipeline behavior is
        // therefore D5 branch A: Encoding.UTF8's default replacement strategy
        // maps the lone surrogate to U+FFFD (EF BF BD) and the rewrite stands.
        // Production code keeps no special case (CS2J5583 stays reserved).
        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(1, result.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(3, result.Statistics.Utf8BytesEmitted);
        Assert.Contains("(byte)0xEF", result.OutputCode);
        Assert.Contains("(byte)0xBF", result.OutputCode);
        Assert.Contains("(byte)0xBD", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL5_MissingExpectedUtf8Blob_FailsFidelity_With5581()
    {
        var recorder = new LiteralRewriteRecorder();
        recorder.RecordUtf8(new byte[] { 0x01, 0x02 });

        var outcome = LiteralNormalizationVerifier.Verify(
            "public class C { }",
            new LexicalNormalizeOptions(),
            recorder);

        Assert.True(outcome.Fatal);
        Assert.Contains(outcome.Diagnostics, d => d.Code == "CS2J5581");
    }

    [Fact]
    public void LL3_Verbatim_DoubledQuotes_Crlf_AndBackslashes_AreRegularStrings()
    {
        var source = "public class C {\n"
            + "public string A = @\"a\"\"b\";\n"
            + "public string B = @\"line1\r\nline2\";\n"
            + "public string P = @\"C:\\path\";\n}";

        var result = Run(source);

        Assert.True(result.Changed);
        Assert.Equal(3, result.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("\"a\\\"b\"", result.OutputCode);
        Assert.Contains("\"line1\\r\\nline2\"", result.OutputCode);
        Assert.Contains("\"C:\\\\path\"", result.OutputCode);
        Assert.DoesNotContain("@\"", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL4_InterpolatedForms_SkippedAndBucketed_HoleLiteralsStillRewritten()
    {
        const string source = """"
            public class C
            {
                public string A(int x) => $"x{x}";
                public string B(int x) => $@"a""{x}";
                public string C(int x) => @$"{x}";
                public string D(int x) => $"""raw {x}""";
                public string E(int x) => $$"""{{x}}""";
                public string F() => $"{("""inner""")}";
            }
            """";

        var result = Run(source);

        Assert.True(result.Changed);
        // A `$"x{x}"` 与 F `$"{(...)}"` 的外层各一个常规插值串；F 洞内的 raw 仍被改写。
        Assert.Equal(2, result.Statistics.InterpolatedRegularSkipped);
        Assert.Equal(2, result.Statistics.InterpolatedVerbatimSkipped);
        Assert.Equal(2, result.Statistics.InterpolatedRawSkipped);
        Assert.Equal(1, result.Statistics.RawLiteralsSingleLine);
        Assert.Contains("$\"x{x}\"", result.OutputCode);
        Assert.Contains("$@\"a\"\"{x}\"", result.OutputCode);
        Assert.Contains("@$\"{x}\"", result.OutputCode);
        Assert.Contains("$\"\"\"raw {x}\"\"\"", result.OutputCode);
        Assert.Contains("$$\"\"\"{{x}}\"\"\"", result.OutputCode);
        Assert.Contains("\"inner\"", result.OutputCode);
        Assert.DoesNotContain("(\"\"\"inner\"\"\")", result.OutputCode);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == LexicalNormalizeSeverity.Error));
    }

    [Fact]
    public void LL_AllRules_SecondRunIsClean_AndByteIdempotent()
    {
        var source = "public class C { public void M() {\n"
            + "string r = \"\"\"\nx\n\"\"\";\n"
            + "System.ReadOnlySpan<byte> u = \"ab\"u8;\n"
            + "string v = @\"a\r\nb\";\n"
            + "string e = \"\\e\";\n} }\n";

        var first = Run(source);
        Assert.True(first.Changed);

        var second = Run(first.OutputCode);
        Assert.False(second.Changed);
        Assert.Equal(1, second.Statistics.FilesSkippedClean);
        Assert.Equal(first.OutputCode, second.OutputCode);
    }

    [Fact]
    public void LL2_LL3_DisableSwitches_OnlyTargetedRulesAreSuppressed()
    {
        var source = "public class C { public void M() { "
            + "System.ReadOnlySpan<byte> u = \"a\"u8; string v = @\"b\"; } }\n";

        var u8Off = Run(source, new LexicalNormalizeOptions { NormalizeUtf8Literals = false });
        Assert.True(u8Off.Changed);
        Assert.Equal(0, u8Off.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(1, u8Off.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("\"a\"u8", u8Off.OutputCode);

        var verbatimOff = Run(source, new LexicalNormalizeOptions { NormalizeVerbatimLiterals = false });
        Assert.True(verbatimOff.Changed);
        Assert.Equal(1, verbatimOff.Statistics.Utf8LiteralsExpanded);
        Assert.Equal(0, verbatimOff.Statistics.VerbatimLiteralsNormalized);
        Assert.Contains("@\"b\"", verbatimOff.OutputCode);
    }
}
