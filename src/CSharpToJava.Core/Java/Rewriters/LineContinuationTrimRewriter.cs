using System.Text.RegularExpressions;

namespace CSharpToJava.Core.Java.Rewriters;

/// <summary>
/// IR rewriter that fixes GPLEX-generated Scanner classes to handle
/// CR-only (<c>\r</c>) line continuations in DOT quoted strings.
///
/// <para>The original C# lexer's <c>TrimString()</c> only strips
/// <c>\&lt;CR&gt;&lt;LF&gt;</c> and <c>\&lt;LF&gt;</c> from accumulated
/// string values. When a DOT file uses CR-only line endings, the backslash
/// leaks into parsed coordinate data, causing
/// <c>NumberFormatException</c>.</para>
///
/// <para>This rewriter makes two changes in the generated Scanner:
/// 1. Separates DFA state 35 from the grouped case and adds a
///    <c>trimString()</c> call.
/// 2. Adds an <c>else if</c> branch to <c>trimString()</c> that strips
///    the trailing <c>\&lt;CR&gt;</c> sequence.</para>
/// </summary>
public sealed class LineContinuationTrimRewriter : JavaSyntaxRewriter
{
    private int _rewriteCount;
    private bool _trimStringFixed;
    private bool _scanCaseFixed;
    private string _currentMethodName = string.Empty;

    public int RewriteCount => _rewriteCount;

    // Substring patterns for trimString detection — indentation-independent.
    private const string CrlfCheckPattern = @"endsWith(""\\\r\n"")";
    private const string LfCheckPattern   = @"endsWith(""\\\n"")";
    private const string CrCheckPattern   = @"endsWith(""\\\r"")";

    // Key text substring for scan() case 35 detection — indentation-independent.
    private const string Case35GroupedKey = @"case 30, 31, 33, 35:";

    // Regex to match the grouped case block with flexible leading whitespace.
    private static readonly Regex ScanCase35Regex = new(
        @"^(\s*)case 30, 31, 33, 35:\s*\n" +
        @"\s*stringId \+= getYytext\(\);\s*\n" +
        @"\s*break;",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private const string LfBranchEndMarker = @"stringId.length() - 2);";

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        _trimStringFixed = false;
        _scanCaseFixed = false;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var previousMethod = _currentMethodName;
        _currentMethodName = node.Name;

        var result = base.VisitMethodDeclaration(node);

        // Check raw Body string (v1 pipeline)
        if (!string.IsNullOrWhiteSpace(node.Body))
        {
            ApplyFixes(node.Body, fixedBody =>
            {
                node.Body = fixedBody;
            });
        }

        _currentMethodName = previousMethod;
        return result;
    }

    public override JavaRawStatement VisitRawStatement(JavaRawStatement node)
    {
        var result = base.VisitRawStatement(node);

        // Handle fixes in raw statements within StructuredBody (v2 pipeline)
        if (!string.IsNullOrWhiteSpace(node.Code))
        {
            ApplyFixes(node.Code, fixedCode =>
            {
                node.Code = fixedCode;
            });
        }

        return result;
    }

    private void ApplyFixes(string code, Action<string> assignBack)
    {
        if (!_trimStringFixed && IsCurrentMethod("trimString") && NeedsTrimStringFix(code))
        {
            assignBack(FixTrimString(code));
            _trimStringFixed = true;
            _rewriteCount++;
        }

        if (!_scanCaseFixed && IsCurrentMethod("scan") && NeedsScanCaseFix(code))
        {
            assignBack(FixScanCase(code));
            _scanCaseFixed = true;
            _rewriteCount++;
        }
    }

    private bool IsCurrentMethod(string name)
        => _currentMethodName.Equals(name, StringComparison.OrdinalIgnoreCase);

    private static bool NeedsTrimStringFix(string body)
        => body.Contains(CrlfCheckPattern)
        && body.Contains(LfCheckPattern)
        && !body.Contains(CrCheckPattern);

    private static bool NeedsScanCaseFix(string body)
        => body.Contains(Case35GroupedKey)
        && !ContainsCase35WithTrimString(body);

    private static bool ContainsCase35WithTrimString(string body)
    {
        int idx = body.IndexOf(@"case 35:", StringComparison.Ordinal);
        if (idx < 0) return false;
        int end = Math.Min(idx + 300, body.Length);
        return body.Substring(idx, end - idx).Contains("trimString();");
    }

    private static string FixTrimString(string body)
    {
        int idx = body.LastIndexOf(LfBranchEndMarker, StringComparison.Ordinal);
        if (idx < 0) return body;

        int closeBrace = body.IndexOf('}', idx + LfBranchEndMarker.Length);
        if (closeBrace < 0) return body;

        string indent = ExtractLineIndent(body, idx);

        string crOnlyBranch =
            indent + @"} else if (stringId.endsWith(""\\\r"")) {" + "\n" +
            indent + @"stringId = stringId.substring(0, stringId.length() - 2);" + "\n" +
            indent;

        return body.Insert(closeBrace, crOnlyBranch);
    }

    private static string FixScanCase(string body)
    {
        var match = ScanCase35Regex.Match(body);
        if (!match.Success) return body;

        string indent = match.Groups[1].Value;

        string replacement =
            indent + "case 30, 31, 33:\n" +
            indent + "stringId += getYytext();\n" +
            indent + "break;\n" +
            indent + "case 35:\n" +
            indent + "stringId += getYytext();\n" +
            indent + "trimString();\n" +
            indent + "break;";

        return ScanCase35Regex.Replace(body, replacement);
    }

    private static string ExtractLineIndent(string text, int charIndex)
    {
        int lineStart = text.LastIndexOf('\n', charIndex > 0 ? charIndex - 1 : 0);
        if (lineStart < 0) lineStart = 0;
        else lineStart++;

        int i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;

        return text.Substring(lineStart, i - lineStart);
    }
}
