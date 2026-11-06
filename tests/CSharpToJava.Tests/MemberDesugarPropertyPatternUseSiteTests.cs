using CSharpToJava.CLI;
using CSharpToJava.Core.MemberDesugar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// PD-6 regression: the property use-site rewriter must not turn a name inside
/// an extended property pattern (<c>o is { Child.Flag: true }</c>) into a
/// <c>get_Child()</c> call. That produces an unparsable pattern (CS1003), the
/// whole file fails local parse validation and is preserved — which in rt10
/// left JsonValue.cs with dangling <c>element.ValueKind</c> reads after the
/// property declaration had been desugared in another file (NORM-ENUM-ARITH on
/// the surrounding switch temp).
/// </summary>
public sealed class MemberDesugarPropertyPatternUseSiteTests
{
    private const string Source = """
    class Inner
    {
        public bool Flag { get; set; }
    }

    class Outer
    {
        public Inner Child { get; set; }
    }

    class Use
    {
        bool Extended(Outer o) => o is { Child.Flag: true };
        bool Nested(Outer o) => o is { Child: { Flag: true } };
        Inner Read(Outer o) => o.Child;
    }
    """;

    [Fact]
    public void ExtendedPropertyPatternPath_IsNotRewrittenToGetterCall()
    {
        var code = Rewrite();

        var squished = Squish(code);
        Assert.Contains("{ Child.Flag: true }", squished);
        Assert.DoesNotContain("{ get_Child().Flag: true }", squished);
        Assert.DoesNotContain("{ get_Child().get_Flag(): true }", squished);

        // Ordinary read sites still desugar.
        Assert.Contains("o.get_Child()", squished);

        // The local parse validation the pass applies must accept the output.
        Assert.Empty(ParseErrors(code));
    }

    private static string Rewrite()
    {
        var tree = (CSharpSyntaxTree)CSharpSyntaxTree.ParseText(
            Source, new CSharpParseOptions(LanguageVersion.Latest), path: "PropertyPattern.cs");
        var compilation = CSharpCompilation.Create(
            "member-desugar-property-pattern-tests",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new MemberDesugarPreprocessor(new MemberDesugarOptions());
        preprocessor.BindCompilation(compilation);
        var statistics = new MemberDesugarStatistics();
        var diagnostics = new List<MemberDesugarDiagnostic>();
        var result = preprocessor.RewriteFile(
            tree.ToString(), compilation.GetSemanticModel(tree), statistics, diagnostics);

        Assert.True(result.Changed, "属性未被展开：" + string.Join("; ", diagnostics));
        return result.OutputCode;
    }

    private static string Squish(string code)
        => System.Text.RegularExpressions.Regex.Replace(code, @"\s+", " ");

    private static string ParseErrors(string code)
        => string.Join("\n", CSharpSyntaxTree
            .ParseText(code, new CSharpParseOptions(LanguageVersion.Latest))
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
}
