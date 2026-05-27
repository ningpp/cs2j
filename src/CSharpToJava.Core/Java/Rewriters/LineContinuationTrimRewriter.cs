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

    /// <summary>Number of rewrites performed during traversal.</summary>
    public int RewriteCount => _rewriteCount;

    // Verbatim strings matching the literal text in the generated Java source.
    // C# verbatim strings treat \r, \n as literal backslash+letter, not escapes.
    private const string CrlfCheckPattern = @"endsWith(""\\\r\n"")";
    private const string LfCheckPattern   = @"endsWith(""\\\n"")";
    private const string CrCheckPattern   = @"endsWith(""\\\r"")";

    private const string Case35GroupedPattern =
        @"case 30, 31, 33, 35:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            break;";

    private static readonly string Case35FixedBlock =
        @"case 30, 31, 33:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            break;" + "\n" +
        @"        case 35:" + "\n" +
        @"            stringId += getYytext();" + "\n" +
        @"            trimString();" + "\n" +
        @"            break;";

    private const string LfBranchEndMarker = @"stringId.length() - 2);";

    private static readonly string CrOnlyBranch =
        @"        } else if (stringId.endsWith(""\\\r"")) {" + "\n" +
        @"            stringId = stringId.substring(0, stringId.length() - 2);";

    public override JavaCompilationUnit VisitCompilationUnit(JavaCompilationUnit node)
    {
        _rewriteCount = 0;
        _trimStringFixed = false;
        _scanCaseFixed = false;
        return base.VisitCompilationUnit(node);
    }

    public override JavaMethodDeclaration VisitMethodDeclaration(JavaMethodDeclaration node)
    {
        var result = base.VisitMethodDeclaration(node);

        if (string.IsNullOrWhiteSpace(node.Body))
            return result;

        if (!_trimStringFixed && IsTrimStringMethod(node))
        {
            if (NeedsTrimStringFix(node.Body))
            {
                node.Body = FixTrimString(node.Body);
                _trimStringFixed = true;
                _rewriteCount++;
            }
        }

        if (!_scanCaseFixed && IsScanMethod(node))
        {
            if (NeedsScanCaseFix(node.Body))
            {
                node.Body = FixScanCase(node.Body);
                _scanCaseFixed = true;
                _rewriteCount++;
            }
        }

        return result;
    }

    private static bool IsTrimStringMethod(JavaMethodDeclaration node)
        => node.Name.Equals("trimString", StringComparison.OrdinalIgnoreCase);

    private static bool IsScanMethod(JavaMethodDeclaration node)
        => node.Name.Equals("scan", StringComparison.OrdinalIgnoreCase);

    private static bool NeedsTrimStringFix(string body)
        => body.Contains(CrlfCheckPattern)
        && body.Contains(LfCheckPattern)
        && !body.Contains(CrCheckPattern);

    private static bool NeedsScanCaseFix(string body)
        => body.Contains(Case35GroupedPattern);

    private static string FixTrimString(string body)
    {
        int idx = body.LastIndexOf(LfBranchEndMarker, StringComparison.Ordinal);
        if (idx < 0)
            return body;

        int closeBrace = body.IndexOf('}', idx + LfBranchEndMarker.Length);
        if (closeBrace < 0)
            return body;

        return body.Insert(closeBrace, CrOnlyBranch);
    }

    private static string FixScanCase(string body)
        => body.Replace(Case35GroupedPattern, Case35FixedBlock);
}
