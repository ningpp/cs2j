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
}
