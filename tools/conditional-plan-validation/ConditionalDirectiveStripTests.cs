using CSharpToJava.Core.ConditionalDirectiveStrip;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.Tests;

public class ConditionalDirectiveStripTests
{
    private static ConditionalDirectiveStripResult Strip(
        string source,
        string[]? symbols = null,
        bool removeRegions = false) =>
        new ConditionalDirectiveStripper().Strip(
            source,
            new ConditionalDirectiveStripOptions
            {
                Symbols = symbols ?? Array.Empty<string>(),
                RemoveRegions = removeRegions,
            });

    private static string Output(string source, string[]? symbols = null, bool removeRegions = false) =>
        Strip(source, symbols, removeRegions).OutputCode;

    private static void AssertSuccessful(
        string source,
        ConditionalDirectiveStripResult result,
        string[]? symbols = null,
        bool removeRegions = false)
    {
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        var parseOptions = CSharpParseOptions.Default
            .WithLanguageVersion(LanguageVersion.Latest)
            .WithPreprocessorSymbols(symbols ?? Array.Empty<string>());
        var inputRoot = CSharpSyntaxTree.ParseText(source, parseOptions).GetCompilationUnitRoot();
        var outputRoot = CSharpSyntaxTree.ParseText(result.OutputCode, parseOptions).GetCompilationUnitRoot();
        Assert.DoesNotContain(outputRoot.SyntaxTree.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        foreach (var trivia in outputRoot.DescendantTrivia(descendIntoTrivia: true))
        {
            Assert.False(trivia.IsKind(SyntaxKind.DisabledTextTrivia), $"disabled text left: {trivia}");
            if (trivia.GetStructure() is DirectiveTriviaSyntax directive)
            {
                Assert.False(
                    ConditionalDirectiveStripper.IsRemovedDirective(directive.Kind(), removeRegions),
                    $"target directive left: {directive}");
            }
        }

        var expectedTokens = inputRoot.DescendantTokens().Select(token => (token.RawKind, token.Text));
        var actualTokens = outputRoot.DescendantTokens().Select(token => (token.RawKind, token.Text));
        Assert.Equal(expectedTokens, actualTokens);
    }

    [Fact]
    public void NoDirectives_IsByteForByteUnchanged()
    {
        const string source = "class C { void M() {} }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Equal(0, result.Statistics.DirectivesRemoved);
        Assert.Equal(0, result.Statistics.DisabledBranchesRemoved);
    }

    [Fact]
    public void IfElseAndElif_SelectExpectedBranches()
    {
        Assert.Equal("X();\n", Output("#if A\nX();\n#endif\n", ["A"]));
        Assert.Equal(string.Empty, Output("#if A\nX();\n#endif\n"));
        Assert.Equal("Y();\n", Output("#if A\nX();\n#else\nY();\n#endif\n"));
        Assert.Equal("B();\n", Output("#if A\nA();\n#elif B\nB();\n#elif C\nC();\n#else\nD();\n#endif\n", ["B"]));
        Assert.Equal("D();\n", Output("#if A\nA();\n#elif B\nB();\n#else\nD();\n#endif\n"));
    }

    [Theory]
    [InlineData(true, true, "OUTER_TRUE();\nINNER_TRUE();\n")]
    [InlineData(true, false, "OUTER_TRUE();\nINNER_FALSE();\n")]
    [InlineData(false, true, "OUTER_FALSE();\nDISABLED_INNER_TRUE();\n")]
    [InlineData(false, false, "OUTER_FALSE();\nDISABLED_INNER_FALSE();\n")]
    public void Nested_AllTruthCombinations(bool outer, bool inner, string expected)
    {
        const string source = "#if OUTER\nOUTER_TRUE();\n#if INNER\nINNER_TRUE();\n#else\nINNER_FALSE();\n#endif\n#else\nOUTER_FALSE();\n#if INNER\nDISABLED_INNER_TRUE();\n#else\nDISABLED_INNER_FALSE();\n#endif\n#endif\n";
        var symbols = new List<string>();
        if (outer) symbols.Add("OUTER");
        if (inner) symbols.Add("INNER");
        var result = Strip(source, symbols.ToArray());
        Assert.Equal(expected, result.OutputCode);
        AssertSuccessful(source, result, symbols.ToArray());
    }

    [Fact]
    public void ConditionalExpressionOperators_AreEvaluatedByRoslyn()
    {
        Assert.Equal("X();\n", Output("#if !A\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#if (A || B) && !C\nX();\n#endif\n", ["B"]));
        Assert.Equal("X();\n", Output("#if A == true\nX();\n#endif\n", ["A"]));
        Assert.Equal("X();\n", Output("#if A != true\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#if true\nX();\n#endif\n"));
        Assert.Equal(string.Empty, Output("#if false\nX();\n#endif\n"));
        Assert.Equal("X();\n", Output("#  if  A\nX();\n#  endif\n", ["A"]));
    }

    [Fact]
    public void SymbolEquality_IsLegalRoslynSyntax()
    {
        const string source = "#if A == B\nX();\n#endif\n";
        Assert.Equal("X();\n", Output(source));
        Assert.Equal(string.Empty, Output(source, ["A"]));
    }

    [Fact]
    public void Symbols_AreCaseSensitive()
    {
        Assert.Equal(string.Empty, Output("#if foo\nX();\n#endif\n", ["FOO"]));
    }

    [Fact]
    public void DefineAndUndef_AffectOnlyFollowingConditions()
    {
        Assert.Equal("X();\n", Output("#define A\n#if A\nX();\n#endif\n"));
        Assert.Equal(string.Empty, Output("#if A\nX();\n#endif\n#define A\n"));
        Assert.Equal("Y();\n", Output("#undef A\n#if A\nX();\n#else\nY();\n#endif\n", ["A"]));
        Assert.Equal("X();\n", Output("#define A\n#if A\nX();\n#endif\n", ["A"]));
    }

    public static TheoryData<string, string, string> PositionCases => new()
    {
        { "#if A\nusing Kept.Namespace;\n#else\nusing Removed.Namespace;\n#endif\nclass C {}", "Kept", "Removed" },
        { "#if A\n[Kept]\n#else\n[Removed]\n#endif\nclass C {}", "Kept", "Removed" },
        { "class C {\n#if A\nvoid Kept() {}\n#else\nvoid Removed() {}\n#endif\n}", "Kept", "Removed" },
        { "class C { public\n#if A\nstatic\n#else\nvirtual\n#endif\nvoid M() {} }", "static", "virtual" },
        { "var value = new List<\n#if A\nint\n#else\nstring\n#endif\n>();", "int", "string" },
        { "class C { void M() {\n#if A\nKept();\n#else\nRemoved();\n#endif\n} }", "Kept", "Removed" },
        { "F(\n#if A\nKept\n#else\nRemoved\n#endif\n);", "Kept", "Removed" },
        { "var value = new C {\n#if A\nP = Kept\n#else\nP = Removed\n#endif\n};", "Kept", "Removed" },
        { "var value = new[] {\n#if A\nKept\n#else\nRemoved\n#endif\n};", "Kept", "Removed" },
        { "var value = x switch {\n#if A\n1 => Kept,\n#else\n_ => Removed,\n#endif\n};", "Kept", "Removed" },
        { "var value = (\n#if A\nint Kept\n#else\nstring Removed\n#endif\n) => 0;", "Kept", "Removed" },
    };

    [Theory]
    [MemberData(nameof(PositionCases))]
    public void Directives_AreHandledAtEverySyntaxPosition(string source, string kept, string removed)
    {
        var result = Strip(source, ["A"]);
        var tokens = CSharpSyntaxTree.ParseText(result.OutputCode).GetCompilationUnitRoot()
            .DescendantTokens().Select(token => token.Text).ToArray();
        Assert.Contains(kept, tokens);
        Assert.DoesNotContain(removed, tokens);
        AssertSuccessful(source, result, ["A"]);
    }

    [Fact]
    public void EofDirectiveWithoutTrailingNewline_PreservesPrecedingNewline()
    {
        const string source = "class C { }\n#if A\n#endif";
        var result = Strip(source);
        Assert.Equal("class C { }\n", result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Fact]
    public void EmptyAndDirectiveOnlyInputs_HaveNoResidualDirectiveLines()
    {
        Assert.Equal(string.Empty, Output("#define A\n"));
        Assert.Equal(string.Empty, Output("#if A\nclass C { }\n#endif\n"));
        Assert.Equal("A();\nB();\n", Output("A();\n    #if A\n    #endif\nB();\n"));
    }

    [Fact]
    public void DirectiveLookalikesInStringsAndComments_AreUnchanged()
    {
        const string source = "var a = \"#if A\";\nvar b = @\"x\n#endif\";\n// #if B\n/* #endif */\n/// <summary>#if C</summary>\nclass C {}";
        var result = Strip(source, ["A"]);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void DisabledGarbageAndNestedDirectives_AreRemoved()
    {
        const string source = "OK();\n#if A\n@@@ nonsense ]]]\n#if B\nX();\n#elif C\nY();\n#endif\n#endif\n";
        var result = Strip(source);
        Assert.Equal("OK();\n", result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Theory]
    [InlineData("#warning hidden")]
    [InlineData("#error hidden")]
    [InlineData("#pragma warning disable CS0168")]
    [InlineData("#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"")]
    [InlineData("#line hidden")]
    [InlineData("#nullable enable")]
    [InlineData("#region R\n#endregion")]
    public void InactiveNonConditionalDirectives_AreRemovedWithTheirBranch(string directive)
    {
        var source = $"#if A\n{directive}\n#endif\n";
        var result = Strip(source);
        Assert.True(result.Changed);
        Assert.Equal(string.Empty, result.OutputCode);
        AssertSuccessful(source, result);
    }

    [Fact]
    public void ActiveNonConditionalDirectives_ArePreservedExactly()
    {
        const string source = "#pragma warning disable CS1591\n#pragma checksum \"x.cs\" \"{406EA660-64CF-4C82-B6F0-42D48172A799}\" \"001122\"\n#line hidden\n#line default\n#nullable enable\nclass C { }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
    }

    [Fact]
    public void Regions_ArePreservedByDefaultAndOptionallyRemoved()
    {
        const string source = "#region R\nclass C { }\n#endregion\n";
        var kept = Strip(source);
        Assert.False(kept.Changed);
        Assert.Equal(source, kept.OutputCode);

        var removed = Strip(source, removeRegions: true);
        Assert.True(removed.Changed);
        Assert.Equal("class C { }\n", removed.OutputCode);
        AssertSuccessful(source, removed, removeRegions: true);
    }

    [Fact]
    public void ActiveWarning_IsPreservedAndProducesExactlyOneWarning()
    {
        const string source = "#warning fixme\nclass C { }";
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal(ConditionalDirectiveSeverity.Warning, warning.Severity);
        Assert.Equal(1, warning.Line);
    }

    public static TheoryData<string> MalformedCases => new()
    {
        "#endif\n",
        "#if A\nX();\n",
        "#if A\n#else\n#elif B\n#endif\n",
        "class C { }\n#define A\n",
        "class C { #if A }\n",
        "#if A + B\nX();\n#endif\n",
        "#error nope\n",
        "#wibble\nclass C { }\n",
        "#r \"x.dll\"\n",
        "#load \"x.csx\"\n",
        "#!/usr/bin/env dotnet\nclass C { }\n",
    };

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void MalformedOrScriptOnlyInput_IsBlocked(string source)
    {
        var result = Strip(source);
        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == ConditionalDirectiveSeverity.Error);
        Assert.Equal(0, result.Statistics.DirectivesRemoved);
        Assert.Equal(0, result.Statistics.DisabledBranchesRemoved);
    }

    [Theory]
    [InlineData("#if A\nX();\n#endif\n", "X();\n")]
    [InlineData("#if A\r\nX();\r\n#endif\r\n", "X();\r\n")]
    [InlineData("#if A\rX();\r#endif\r", "X();\r")]
    [InlineData("#if A\r\nX();\nY();\r#endif\r\n", "X();\nY();\r")]
    public void LineEndings_ArePreserved(string source, string expected)
    {
        Assert.Equal(expected, Output(source, ["A"]));
    }

    [Fact]
    public void Statistics_CountRemovedDirectiveTriviaAndDisabledTextBlocks()
    {
        var result = Strip("#define A\n#if A\nX();\n#else\nY();\n#endif\n");
        Assert.True(result.Changed);
        Assert.Equal(4, result.Statistics.DirectivesRemoved);
        Assert.Equal(1, result.Statistics.DisabledBranchesRemoved);
    }
}
