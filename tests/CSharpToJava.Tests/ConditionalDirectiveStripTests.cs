using CSharpToJava.Core.ConditionalDirectiveStrip;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

/// <summary>
/// Conditional directive stripper coverage driven by the design matrices in
/// docs/superpowers/specs/2026-09-24-conditional-directive-strip-design.md (§3/§4).
/// </summary>
public class ConditionalDirectiveStripTests
{
    private static ConditionalDirectiveStripResult Strip(
        string src, string[]? symbols = null, bool removeRegions = false)
    {
        var options = new ConditionalDirectiveStripOptions
        {
            Symbols = symbols ?? Array.Empty<string>(),
            RemoveRegions = removeRegions,
        };
        return new ConditionalDirectiveStripper().Strip(src, options);
    }

    private static string Out(string src, string[]? symbols = null, bool removeRegions = false)
        => Strip(src, symbols, removeRegions).OutputCode;

    /// <summary>Output must parse error-free and contain no conditional directives / disabled text.</summary>
    private static void AssertClean(ConditionalDirectiveStripResult result, string[]? symbols = null)
    {
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == ConditionalDirectiveSeverity.Error));
        var tree = CSharpSyntaxTree.ParseText(
            result.OutputCode,
            CSharpParseOptions.Default.WithPreprocessorSymbols(symbols ?? Array.Empty<string>()));
        Assert.Empty(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        foreach (var token in tree.GetCompilationUnitRoot().DescendantTokens())
        {
            foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
            {
                Assert.False(trivia.IsKind(SyntaxKind.DisabledTextTrivia), $"disabled text left: {trivia}");
                if (trivia.GetStructure() is DirectiveTriviaSyntax directive)
                {
                    Assert.False(
                        ConditionalDirectiveStripper.IsRemovedDirective(directive.Kind()),
                        $"conditional directive left: {directive}");
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Basics
    // -----------------------------------------------------------------------

    [Fact]
    public void NoDirectives_Unchanged()
    {
        var src = "class C { void M() {} }";
        var result = Strip(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void IfTrue_KeepsBranch_AndRemovesDirectives()
    {
        var result = Strip("#if A\nX();\n#endif\n", new[] { "A" });
        Assert.True(result.Changed);
        Assert.Equal("X();\n", result.OutputCode);
        AssertClean(result, new[] { "A" });
    }

    [Fact]
    public void IfFalse_RemovesBranch()
    {
        var result = Strip("#if A\nX();\n#endif\n");
        Assert.Equal("", result.OutputCode);
        AssertClean(result);
    }

    [Fact]
    public void Else_KeptWhenConditionFalse()
    {
        Assert.Equal("Y();\n", Out("#if A\nX();\n#else\nY();\n#endif\n"));
    }

    [Fact]
    public void Else_DroppedWhenConditionTrue()
    {
        Assert.Equal("X();\n", Out("#if A\nX();\n#else\nY();\n#endif\n", new[] { "A" }));
    }

    [Fact]
    public void ElifChain_SelectsFirstTrue()
    {
        var output = Out("#if A\nA();\n#elif B\nB();\n#elif C\nC();\n#else\nD();\n#endif\n", new[] { "B" });
        Assert.Equal("B();\n", output);
    }

    [Fact]
    public void ElifChain_AllFalse_SelectsElse()
    {
        Assert.Equal("D();\n", Out("#if A\nA();\n#elif B\nB();\n#else\nD();\n#endif\n"));
    }

    [Fact]
    public void Nested_OuterTrueInnerTrue()
    {
        var src = "#if A\nOUTER();\n#if B\nINNER();\n#endif\n#endif\n";
        Assert.Equal("OUTER();\nINNER();\n", Out(src, new[] { "A", "B" }));
    }

    [Fact]
    public void Nested_OuterTrueInnerFalse()
    {
        var src = "#if A\nKEEP();\n#if B\nDROP();\n#endif\n#endif\n";
        Assert.Equal("KEEP();\n", Out(src, new[] { "A" }));
    }

    [Fact]
    public void Nested_OuterFalse_InnerGarbageSwallowed()
    {
        var result = Strip("#if A\n$$$ garbage ((\n#if B\n#endif\n#endif\n");
        Assert.Equal("", result.OutputCode);
        AssertClean(result);
    }

    // -----------------------------------------------------------------------
    // Conditional expressions
    // -----------------------------------------------------------------------

    [Fact]
    public void NotOperator()
    {
        Assert.Equal("X();\n", Out("#if !A\nX();\n#endif\n"));
    }

    [Fact]
    public void AndOrParens()
    {
        var src = "#if (A || B) && !C\nX();\n#endif\n";
        Assert.Equal("X();\n", Out(src, new[] { "B" }));
        Assert.Equal("X();\n", Out(src, new[] { "A" }));
        Assert.Equal("", Out(src, new[] { "B", "C" }));
        Assert.Equal("", Out(src, new[] { "A", "C" }));
        Assert.Equal("", Out(src));
    }

    [Fact]
    public void EqualsTrue()
    {
        Assert.Equal("X();\n", Out("#if A == true\nX();\n#endif\n", new[] { "A" }));
    }

    [Fact]
    public void NotEqualsTrue()
    {
        Assert.Equal("X();\n", Out("#if A != true\nX();\n#endif\n"));
    }

    [Fact]
    public void TrueLiteral()
    {
        Assert.Equal("X();\n", Out("#if true\nX();\n#endif\n"));
    }

    [Fact]
    public void FalseLiteral()
    {
        Assert.Equal("", Out("#if false\nX();\n#endif\n"));
    }

    [Fact]
    public void WhitespaceTolerantForms()
    {
        Assert.Equal("X();\n", Out("#  if  A\nX();\n#  endif\n", new[] { "A" }));
    }

    [Fact]
    public void Symbols_CaseSensitive()
    {
        Assert.Equal("", Out("#if foo\nX();\n#endif\n", new[] { "FOO" }));
    }

    // -----------------------------------------------------------------------
    // #define / #undef semantics
    // -----------------------------------------------------------------------

    [Fact]
    public void InFileDefine_ActivatesLaterBranch()
    {
        Assert.Equal("X();\n", Out("#define A\n#if A\nX();\n#endif\n"));
    }

    [Fact]
    public void InFileUndef_RemovesExternalSymbol()
    {
        var src = "#undef A\n#if A\nX();\n#else\nY();\n#endif\n";
        Assert.Equal("Y();\n", Out(src, new[] { "A" }));
    }

    [Fact]
    public void UndefAfterToken_Blocked()
    {
        // #undef 必须先于文件中首个 token（spec），否则 CS1032 → 阻断
        AssertBlocked("#define B\nX();\n#undef B\n");
    }

    [Fact]
    public void RedefineExternalSymbol_Idempotent()
    {
        Assert.Equal("X();\n", Out("#define A\n#if A\nX();\n#endif\n", new[] { "A" }));
    }

    // -----------------------------------------------------------------------
    // Syntactic positions
    // -----------------------------------------------------------------------

    [Fact]
    public void DirectivesInsideMethodBody()
    {
        var src = "class C { void M() {\n#if A\nint x = 1;\n#endif\n} }";
        Assert.Contains("int x = 1;", Out(src, new[] { "A" }));
    }

    [Fact]
    public void DirectivesSplitArgumentList()
    {
        var src = "F(\n#if A\n1\n#else\n2\n#endif\n);";
        Assert.Equal("F(1);", Out(src, new[] { "A" }).Replace("\n", ""));
        Assert.Equal("F(2);", Out(src).Replace("\n", ""));
    }

    [Fact]
    public void DirectivesBetweenModifiers()
    {
        var src = "class C {\npublic\n#if A\nstatic\n#endif\nvoid M() { }\n}";
        Assert.Contains("public static void M", Out(src, new[] { "A" }).Replace("\n", " "));
        Assert.Contains("public void M", Out(src).Replace("\n", " "));
    }

    [Fact]
    public void DirectivesInsideTypeArgumentList()
    {
        var src = "var l = new List<\n#if A\nint\n#else\nstring\n#endif\n>();";
        Assert.Contains("List<int>", Out(src, new[] { "A" }).Replace("\n", ""));
    }

    [Fact]
    public void DirectiveAtEof_WithoutTrailingNewline()
    {
        Assert.Equal("class C { }\n", Out("class C { }\n#if A\n#endif"));
    }

    [Fact]
    public void DirectiveOnlyFile_BecomesEmpty()
    {
        Assert.Equal("", Out("#define A\n"));
    }

    [Fact]
    public void EntireFileDisabled_BecomesEmpty()
    {
        Assert.Equal("", Out("#if A\nclass C { }\n#endif\n"));
    }

    [Fact]
    public void EmptyBranch_LeavesNoDirectiveResidue()
    {
        Assert.Equal("A();\nB();\n", Out("A();\n    #if A\n    #endif\nB();\n"));
    }

    [Fact]
    public void DirectivesAroundUsingsAndAttributes()
    {
        var src = """
            #if A
            using System;
            #endif
            #if B
            [Obsolete]
            #endif
            class C { }
            """;
        var output = Out(src, new[] { "A" });
        Assert.Contains("using System;", output);
        Assert.DoesNotContain("Obsolete", output);
    }

    // -----------------------------------------------------------------------
    // Directive-lookalike text in strings/comments is not directives
    // -----------------------------------------------------------------------

    [Fact]
    public void StringLiteral_LookalikeDirective_Kept()
    {
        var src = "var s = \"#if A\";";
        var result = Strip(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void VerbatimString_SpanningLines_LookalikeKept()
    {
        var src = "var s = @\"a\n#if A\nb\";";
        Assert.Equal(src, Out(src, new[] { "A" }));
    }

    [Fact]
    public void Comments_AndDocComments_WithLookalike_Kept()
    {
        var src = "// #if A\n/* #endif */\n/// <summary>#if B</summary>\nclass C { }";
        Assert.Equal(src, Out(src));
    }

    // -----------------------------------------------------------------------
    // Disabled text
    // -----------------------------------------------------------------------

    [Fact]
    public void DisabledBranch_GarbageSyntax_Removed()
    {
        Assert.Equal("OK();\n", Out("OK();\n#if A\n@@@ nonsense ]]]\n#endif\n"));
    }

    [Fact]
    public void DisabledBranch_NestedDirectives_RemovedEntirely()
    {
        var result = Strip("#if A\n#if B\nX();\n#elif C\nY();\n#endif\n#endif\n");
        Assert.Equal("", result.OutputCode);
        AssertClean(result);
    }

    [Fact]
    public void DisabledBranch_RegionInside_RemovedWithRegion()
    {
        var result = Strip("#if A\n#region R\nx\n#endregion\n#endif\n");
        Assert.Equal("", result.OutputCode);
        AssertClean(result);
    }

    // -----------------------------------------------------------------------
    // Non-conditional directives: preserved by default, optional region removal
    // -----------------------------------------------------------------------

    [Fact]
    public void RegionOnly_DefaultKept_Unchanged()
    {
        var src = "#region R\nclass C { }\n#endregion\n";
        var result = Strip(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void RemoveRegions_Option_RemovesRegions()
    {
        Assert.Equal("class C { }\n", Out("#region R\nclass C { }\n#endregion\n", removeRegions: true));
    }

    [Fact]
    public void PragmaLineNullablePreserved()
    {
        var src = "#pragma warning disable CS1591\n#line 10 \"x\"\n#nullable enable\nclass C { }";
        var result = Strip(src);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
    }

    [Fact]
    public void WarningDirective_Preserved_WithWarningDiagnostic()
    {
        var src = "#warning fixme\nclass C { }";
        var result = Strip(src);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Severity == ConditionalDirectiveSeverity.Warning);
    }

    // -----------------------------------------------------------------------
    // Malformed input must be blocked (error diagnostics, file unchanged)
    // -----------------------------------------------------------------------

    private static void AssertBlocked(string src, string[]? symbols = null)
    {
        var result = Strip(src, symbols);
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        Assert.Contains(result.Diagnostics, d => d.Severity == ConditionalDirectiveSeverity.Error);
    }

    [Fact]
    public void ExtraEndIf_Blocked() => AssertBlocked("#endif\n");

    [Fact]
    public void MissingEndIf_Blocked() => AssertBlocked("#if A\nX();\n");

    [Fact]
    public void ElifAfterElse_Blocked() => AssertBlocked("#if A\nX();\n#else\nY();\n#elif B\nZ();\n#endif\n");

    [Fact]
    public void DefineAfterToken_Blocked() => AssertBlocked("class C { }\n#define A\n");

    [Fact]
    public void DirectiveNotFirstOnLine_Blocked() => AssertBlocked("class C { #if A }\n");

    [Fact]
    public void SymbolEqualityExpression_RoslynLenient()
    {
        // spec 只允许与 true/false 比较，但 Roslyn 宽容求值（按符号定义与否相等比较）：
        // 两者都未定义 → 相等 → 分支保留；仅定义其一 → 不等 → 分支删除。
        var src = "#if A == B\nX();\n#endif\n";
        Assert.Equal("X();\n", Out(src));
        Assert.Equal("", Out(src, new[] { "A" }));
    }

    [Fact]
    public void ActiveErrorDirective_Blocked() => AssertBlocked("#error nope\nclass C { }");

    [Fact]
    public void BadDirective_Blocked() => AssertBlocked("#wibble\nclass C { }");

    // -----------------------------------------------------------------------
    // Line endings & statistics
    // -----------------------------------------------------------------------

    [Fact]
    public void CrlfPreserved()
    {
        Assert.Equal("X();\r\n", Out("#if A\r\nX();\r\n#endif\r\n", new[] { "A" }));
    }

    [Fact]
    public void LfPreserved()
    {
        Assert.Equal("X();\n", Out("#if A\nX();\n#endif\n", new[] { "A" }));
    }

    [Fact]
    public void MixedLineEndings_PreservedVerbatim()
    {
        var src = "#if A\r\nX();\nY();\r\n#endif\r\n";
        Assert.Equal("X();\nY();\r\n", Out(src, new[] { "A" }));
    }

    [Fact]
    public void Statistics_Counted()
    {
        var result = Strip("#define A\n#if A\nX();\n#else\nY();\n#endif\n");
        Assert.Equal(4, result.Statistics.DirectivesRemoved);
        Assert.Equal(1, result.Statistics.DisabledBranchesRemoved);
    }

    [Fact]
    public void KeptBranch_CommentsAndDocComments_Preserved()
    {
        var src = "#if A\n// keep me\n/// <summary>doc</summary>\nX(); // trailing\n#endif\n";
        var output = Out(src, new[] { "A" });
        Assert.Contains("// keep me", output);
        Assert.Contains("<summary>doc</summary>", output);
        Assert.Contains("X(); // trailing", output);
    }
}
