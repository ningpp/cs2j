using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.Pipeline;

namespace CSharpToJava.Tests;

/// <summary>
/// Iteration 24: verify Roslyn token value handling for surrogate-pair escapes
/// (RegexCharClass category ranges like "\uD800\uDB80" became U+FFFD in output).
/// </summary>
public class SurrogateLiteralProbeTests
{
    [Fact]
    public void RoslynTokenValueText_KeepsSurrogates()
    {
        var tree = CSharpSyntaxTree.ParseText(@"class C { const string S = ""\uD800\uDB80""; }");
        var root = tree.GetRoot();
        var literal = root.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LiteralExpressionSyntax>()
            .First(n => n.Token.ValueText.Length == 2);
        Assert.Equal(0xD800, literal.Token.ValueText[0]);
        Assert.Equal(0xDB80, literal.Token.ValueText[1]);
    }

    [Fact]
    public void ConverterEmitsSurrogateEscapes()
    {
        var pipeline = new ConversionPipeline();
        var result = pipeline.Convert(new ConversionRequest
        {
            SourceCode = """
class C {
    static readonly string[] S = new[] { "IsHighSurrogates", "\uD800\uDB80" };
}
""",
            FileName = "Sample.cs",
            Options = new ConversionOptions(),
        });
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        // .NET's UTF-8 encoder replaces unpaired surrogates with U+FFFD when writing
        // output files, so the converter must emit \uXXXX escapes for surrogate units.
        Assert.Contains("\"\\uD800\\uDB80\"", result.GeneratedCode ?? "");
    }
}
