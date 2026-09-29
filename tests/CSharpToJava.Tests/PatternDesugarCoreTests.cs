using System.Text.RegularExpressions;
using CSharpToJava.Core.PatternDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C03 statement-side pattern desugaring: pattern switch → if-chain, non-integer
/// governing → == chain, catch filters/type-params → guarded bodies/merged chains.
/// Bindings use the hoist-and-assign form (C# pattern variables scope to the
/// enclosing block, so `is T v` conditions cannot be redeclared or shadowed).
/// Positive cases assert normalized text AND that the output is compilable C#
/// with zero residual target syntax.
/// </summary>
public class PatternDesugarCoreTests
{
    private static readonly string RuntimeDir =
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(RuntimeDir, "System.Runtime.dll")),
    };

    private static PatternDesugarResult Desugar(
        string source, bool withModel = true, bool strict = true)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("pattern-desugar-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = withModel ? compilation.GetSemanticModel(tree) : null;
        return new PatternDesugarPreprocessor().Preprocess(
            source, model, new PatternDesugarOptions(Strict: strict));
    }

    private static string Normalize(string text)
        => Regex.Replace(text, @"\s+", " ").Trim();

    private static void AssertCompilesAndClean(PatternDesugarResult result)
    {
        Assert.True(result.Changed);
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var compilation = CSharpCompilation.Create("pd-output",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "output should compile:\n" + result.OutputCode + "\n" +
            string.Join("\n", errors.Select(e => e.ToString())));
        var root = (CompilationUnitSyntax)tree.GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<CasePatternSwitchLabelSyntax>());
        Assert.Empty(root.DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Where(c => c.Filter != null));
    }

    private const string PointAndQ = """
class P { public int X; }
class Q { public string Name { get; set; } }
""";

    // ── pattern switch → if chain ──────────────────────────────────────────

    [Fact]
    public void PatternSwitch_DeclarationAndTypeLabels_BecomeIfChain()
    {
        var result = Desugar("""
class C {
    int M(object o) {
        switch (o) {
            case string s: return 1;
            case int i: return 2;
            default: return 0;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("string s;", text);
        Assert.Contains("int i;", text);
        Assert.Contains("o is string && (s = (string)o) != null", text);
        Assert.Contains("o is int && (i = (int)o) == i", text);
        Assert.DoesNotContain("switch", text);
        Assert.Contains("else", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenClauses_SameVariableName_SharesHoistedBinding()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case int i when i > 0: System.Console.WriteLine("pos"); break;
            case int i when i < 0: System.Console.WriteLine("neg"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        // One hoisted `int i;` serves both branches; each condition assigns it.
        Assert.Contains("int i;", text);
        Assert.Contains("o is int && (i = (int)o) == i && (i > 0)", text);
        Assert.Contains("o is int && (i = (int)o) == i && (i < 0)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_ConstantNull_OnObjectGoverning()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case null: System.Console.WriteLine("null"); break;
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o == null", text);
        Assert.Contains("o is string && (s = (string)o) != null", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_MultipleLabels_ParenthesizedDisjunction()
    {
        var result = Desugar("""
class C {
    void M(int x) {
        switch (x) {
            case 1: case 2: System.Console.WriteLine("low"); break;
            case int i when i > 100: System.Console.WriteLine("big"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("(x == 1 || x == 2)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenOrClause_GuardParenthesizedAsWhole()
    {
        // Precedence regression guard (SwitchCaseWhenClausePrecedenceTests invariant).
        var result = Desugar("""
class C {
    bool M(int type, string str) {
        switch (type) {
            case 1 when str == "A" || str == "B": return true;
            default: return false;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("type == 1 && (str == \"A\" || str == \"B\")", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_WhenAndClause_KeepsSingleParens()
    {
        var result = Desugar("""
class C {
    bool M(int type, int m, int ch) {
        switch (type) {
            case 1 when m > 0 && ch != 5: return true;
            default: return false;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("type == 1 && (m > 0 && ch != 5)", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NoDefault_NoElse()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            case int i: System.Console.WriteLine(i); break;
        }
    }
}
""");
        // No TERMINAL else: every else clause must be an else-if.
        var root = CSharpSyntaxTree.ParseText(result.OutputCode).GetRoot();
        Assert.Empty(root.DescendantNodes().OfType<ElseClauseSyntax>()
            .Where(e => e.Statement is not IfStatementSyntax));
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_Default_Else()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            default: System.Console.WriteLine("other"); break;
        }
    }
}
""");
        Assert.Contains("else", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_TerminatorsMixed_NoEndLabelWithoutBreaks()
    {
        var result = Desugar("""
class C {
    int M(object o, bool flag) {
        switch (o) {
            case string s: return 1;
            case int i: throw new System.InvalidOperationException();
            case bool b when flag: return 2;
        }
        return -1;
    }
}
""");
        Assert.DoesNotContain("__cs2jSwitchEnd", result.OutputCode);
        Assert.DoesNotContain("goto", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_Break_BecomesGotoEndLabel()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
            case int i: System.Console.WriteLine(i); break;
        }
        System.Console.WriteLine("after");
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("goto __cs2jSwitchEnd_0;", text);
        Assert.Contains("__cs2jSwitchEnd_0: ;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NestedLoopBreak_NotHijacked()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s:
                while (true) { break; }
                break;
            case int i: break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        // The loop's break stays a break; the section's own break becomes goto.
        Assert.Contains("while (true) { break; }", text);
        Assert.Contains("goto __cs2jSwitchEnd_0;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_NestedPatternSwitch_BothLowered()
    {
        var result = Desugar("""
class C {
    void M(object o, object p) {
        switch (o) {
            case string s:
                switch (p) {
                    case int j: System.Console.WriteLine(j); break;
                }
                break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("o is string && (s = (string)o) != null", text);
        Assert.Contains("p is int && (j = (int)p) == j", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_PropertyPattern_FieldAndProperty()
    {
        var result = Desugar($$"""
class C {
    int M(P p, Q q) {
        switch (p) {
            case P { X: 1 }: return 1;
            case { X: 2 }: return 2;
        }
        switch (q) {
            case Q { Name: "a" }: return 3;
        }
        return 0;
    }
}
{{PointAndQ}}
""");
        var text = Normalize(result.OutputCode);
        // Type-ful recursive pattern: synthetic hoisted binding + member condition.
        Assert.Contains("__cs2jPat_0 = (P)p", text);
        Assert.Contains("__cs2jPat_0.X == 1", text);
        // Type-less property pattern on reference-typed governing: null guard + direct access.
        Assert.Contains("p != null && p.X == 2", text);
        Assert.Contains("__cs2jPat_1", text);
        Assert.Contains(".Name == \"a\"", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_ObjectGoverning_ConstantUsesTypeTestForm()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case 1: System.Console.WriteLine("one"); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        // `o == 1` would box and compare by reference (always false) — must be is-form.
        Assert.Contains("o is int && (__cs2jPat_0 = (int)o) == 1", text);
        Assert.DoesNotContain("o == 1", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PatternSwitch_RelationalOrAndNotVar_Labels()
    {
        var result = Desugar("""
class C {
    int M(int x, object o) {
        switch (x) {
            case > 0: return 1;
            case >= 0 and <= 10: return 2;
        }
        switch (o) {
            case not null: return 3;
            case var v: return 4;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("x > 0", text);
        Assert.Contains("(x >= 0 && x <= 10)", text);
        Assert.Contains("!(o == null)", text);
        Assert.Contains("var v = o;", text);
        AssertCompilesAndClean(result);
    }

    // ── non-integer governing (D4) ─────────────────────────────────────────

    [Theory]
    [InlineData("bool flag", "true", "flag == true")]
    [InlineData("long l", "5L", "l == 5L")]
    [InlineData("ulong ul", "6UL", "ul == 6UL")]
    [InlineData("float f", "1.5f", "f == 1.5f")]
    [InlineData("double d", "2.5", "d == 2.5")]
    [InlineData("decimal m", "3.5m", "m == 3.5m")]
    public void NonIntegerGoverning_ConstantLabels_BecomeEqualityChain(
        string decl, string constant, string expected)
    {
        var name = decl.Split(' ')[1];
        var result = Desugar($$"""
class C {
    void M({{decl}}) {
        switch ({{name}}) {
            case {{constant}}: System.Console.WriteLine("hit"); break;
            default: break;
        }
    }
}
""");
        Assert.Contains(expected, Normalize(result.OutputCode));
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void PlainSwitches_IntCharStringEnum_AreByteLevelPreserved()
    {
        const string source = """
enum E { A, B }
class C {
    void M(int i, char ch, string s, E e) {
        switch (i) { case 1: break; }
        switch (ch) { case 'a': break; }
        switch (s) { case "x": break; }
        switch (e) { case E.A: break; }
    }
    void N(System.Exception ex) { try { M(1, 'a', "", E.A); } catch (System.IO.IOException) { } }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.True(result.Statistics.FilesDirtyNoRewrite > 0);
    }

    // ── governing single-evaluation (D2/D11) ───────────────────────────────

    [Fact]
    public void Governing_MethodCall_HoistedExactlyOnce()
    {
        var result = Desugar("""
class C {
    int counter;
    int Get() { counter++; return counter; }
    void M() {
        switch (Get()) {
            case int i: System.Console.WriteLine(i); break;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("var __cs2jGov_0 = Get();", text);
        // Two occurrences total: the method declaration plus exactly one hoisted call.
        Assert.Equal(2, Regex.Matches(text, "Get\\(\\)").Count);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void Governing_SimpleIdentifier_NotHoisted()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""");
        Assert.DoesNotContain("__cs2jGov", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    // ── dirty/no-op paths ───────────────────────────────────────────────────

    [Fact]
    public void CleanFile_IsByteLevelPreserved()
    {
        const string source = "class C { int M() { return 1; } }";
        var result = Desugar(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.True(result.Statistics.FilesSkippedClean > 0);
    }

    [Fact]
    public void NoModel_Path_StillLowersPatternLabels()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case string s: System.Console.WriteLine(s); break;
        }
    }
}
""", withModel: false);
        Assert.True(result.Changed);
        Assert.Contains("is string", result.OutputCode);
    }

    [Fact]
    public void NoModel_Path_DoesNotLowerNonIntegerGoverning()
    {
        const string source = """
class C {
    void M(long l) {
        switch (l) { case 5L: break; }
    }
}
""";
        var result = Desugar(source, withModel: false);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    // ── catch-when strategy ①: preserve body (D8①/D10) ─────────────────────

    [Fact]
    public void CatchFilter_SingleClause_PreservesDeclarationAndWrapsBody()
    {
        var result = Desugar("""
class C {
    bool M() {
        try {
            throw new System.InvalidOperationException("boom");
        }
        catch (System.InvalidOperationException ex) when (ex.Message == "expected") {
            System.Console.WriteLine("caught");
            return true;
        }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (System.InvalidOperationException ex)", text);
        Assert.DoesNotContain("when", text);
        Assert.Contains("if (ex.Message == \"expected\")", text);
        Assert.Contains("else throw;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_SingleClause_FinallyPreserved()
    {
        var result = Desugar("""
class C {
    bool M() {
        try {
            return true;
        }
        catch (System.InvalidOperationException ex) when (ex.Message == "x") {
            return false;
        }
        finally {
            System.Console.WriteLine("cleanup");
        }
    }
}
""");
        Assert.Contains("finally", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    // ── catch-when strategy ②: merged chain (D8②) ──────────────────────────

    [Fact]
    public void CatchFilter_SameTypeTwoClauses_FilterFalseFallsToNext()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.Exception ex) when (ex.Message == "a") { System.Console.WriteLine("first"); }
        catch (System.Exception ex) { System.Console.WriteLine("second"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (System.Exception __cs2jEx_0)", text);
        // One hoisted `Exception ex;` serves both branches (no CS0136 redeclaration).
        Assert.Contains("System.Exception ex;", text);
        Assert.Contains("__cs2jEx_0 is System.Exception && (ex = (System.Exception)__cs2jEx_0) != null && (ex.Message == \"a\")", text);
        var firstIndex = text.IndexOf("\"first\"", StringComparison.Ordinal);
        var secondIndex = text.IndexOf("\"second\"", StringComparison.Ordinal);
        Assert.True(firstIndex >= 0 && secondIndex > firstIndex, text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_MultiType_MultiFilter_OrderPreserved()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.FormatException f) when (f.Message != null) { System.Console.WriteLine("f"); }
        catch (System.IO.IOException io) when (io.HResult > 0) { System.Console.WriteLine("io"); }
        catch (System.Exception e) { System.Console.WriteLine("e"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        var i1 = text.IndexOf("f.Message != null", StringComparison.Ordinal);
        var i2 = text.IndexOf("io.HResult > 0", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i2 > i1, text);
        Assert.Contains("System.FormatException f;", text);
        Assert.Contains("System.IO.IOException io;", text);
        Assert.Contains("System.Exception e;", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchFilter_TypelessCatch_IsFinalElseWithoutThrow()
    {
        var result = Desugar("""
class C {
    void M() {
        try { System.Console.WriteLine(); }
        catch (System.FormatException f) when (f.Message != null) { System.Console.WriteLine("f"); }
        catch { System.Console.WriteLine("catch-all"); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("is System.FormatException", text);
        Assert.Contains("catch-all", text);
        // The typeless clause terminates the chain: no trailing rethrow at all.
        Assert.DoesNotContain("throw;", result.OutputCode);
        AssertCompilesAndClean(result);
    }

    // ── catch T (D9) ────────────────────────────────────────────────────────

    [Fact]
    public void CatchTypeParameter_Constrained_LowersToAssignBinding()
    {
        var result = Desugar("""
class C {
    void M<T>(T arg) where T : System.Exception {
        try { System.Console.WriteLine(arg); }
        catch (T ex) { System.Console.WriteLine(ex); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("catch (System.Exception __cs2jEx_0)", text);
        // Type parameters use the self-compare form (null comparison would not compile).
        Assert.Contains("__cs2jEx_0 is T && (ex = (T)(object)__cs2jEx_0) == ex", text);
        Assert.DoesNotContain("catch (T", text);
        AssertCompilesAndClean(result);
    }

    [Fact]
    public void CatchTypeParameter_WithFilter_CombinesTypeTestAndGuard()
    {
        // NOTE: `catch (T)` requires `where T : Exception` — an unconstrained
        // type parameter is CS0155, so the input itself would not compile.
        var result = Desugar("""
class C {
    void M<T>(T arg) where T : System.Exception {
        try { System.Console.WriteLine(arg); }
        catch (T ex) when (ex != null) { System.Console.WriteLine(ex); }
    }
}
""");
        var text = Normalize(result.OutputCode);
        Assert.Contains("__cs2jEx_0 is T && (ex = (T)(object)__cs2jEx_0) == ex && (ex != null)", text);
        AssertCompilesAndClean(result);
    }

    // ── diagnostics ────────────────────────────────────────────────────────

    [Fact]
    public void ListPatternLabel_ReportsPD010_AndLeavesSwitchUntouched()
    {
        const string source = """
class C {
    void M(object o) {
        switch (o) {
            case [1, ..]: System.Console.WriteLine("list"); break;
        }
    }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed, "the switch must not be lowered");
        Assert.Equal(source, result.OutputCode);
        var pd010 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern);
        Assert.Equal(PatternDesugarSeverity.Error, pd010.Severity);
        Assert.True(result.Statistics.UnsupportedSkipped > 0);
    }

    [Fact]
    public void PositionalPatternLabel_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) {
            case (1, 2): System.Console.WriteLine("tuple"); break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("positional"));
    }

    [Fact]
    public void GuardWithIsPattern_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(object o, object p) {
        switch (o) {
            case string s when p is 5: System.Console.WriteLine(s); break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("when guard"));
    }

    [Fact]
    public void GotoDefaultInsidePatternSwitch_ReportsPD011_AndLeavesSwitch()
    {
        const string source = """
class C {
    void M(int x, bool y) {
        switch (x) {
            case int i when y: goto default;
            case int i when !y: break;
            default: break;
        }
    }
}
""";
        var result = Desugar(source);
        Assert.False(result.Changed);
        var pd011 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.GotoCaseInLoweredSwitch);
        Assert.Equal(PatternDesugarSeverity.Error, pd011.Severity);
    }

    [Fact]
    public void NullConstantOnValueTypeGoverning_ReportsPD010()
    {
        var result = Desugar("""
class C {
    void M(int x) {
        switch (x) {
            case null when true: break;
        }
    }
}
""");
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern
            && d.Message.Contains("non-nullable value-type"));
    }

    [Fact]
    public void ResidualCounter_FindsTargetNodes_InRawCode()
    {
        const string source = """
class C {
    void M(object o) {
        switch (o) { case string s: break; }
    }
    void N(System.Exception ex) { try { } catch (System.Exception e) when (e.Message != null) { } }
}
""";
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("residual",
            new[] { tree }, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        var count = PatternDesugarAnalyzer.CountResidualTargets(source, model);
        // one pattern label + one object-governed switch + one catch filter
        Assert.Equal(3, count);
    }

    [Fact]
    public void NonStrict_DowngradesPD010ToWarning()
    {
        var result = Desugar("""
class C {
    void M(object o) {
        switch (o) { case (1, 2): break; }
    }
}
""", strict: false);
        Assert.False(result.Changed);
        var pd010 = Assert.Single(result.Diagnostics, d => d.Code == PatternDesugarCodes.UnsupportedPattern);
        Assert.Equal(PatternDesugarSeverity.Warning, pd010.Severity);
    }
}
