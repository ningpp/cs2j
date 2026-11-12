using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// C35 regression: the null-forgiving operator (<c>!</c>) is transparent for
/// runtime semantics (it only affects nullable analysis), so a chain whose
/// middle segment carries it (<c>a?.b!.c()</c>) must still lower normally
/// instead of leaving an unpaired residual conditional access (CS2J0566).
/// </summary>
public sealed class NullConditionalNullForgivingTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    private const string Source = """
        namespace C35Forgiving;

        public sealed class Node
        {
            public Node? Parent { get; set; }
            public void RemoveChild(Node child) { }
        }

        public sealed class Holder
        {
            public void Remove(Node? signatureNode)
            {
                signatureNode?.Parent!.RemoveChild(signatureNode);
            }
        }
        """;

    private static ExpressionDesugarResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "c35-forgiving-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var model = compilation.GetSemanticModel(tree);
        Assert.Empty(compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions(),
            });
    }

    [Fact]
    public void NullForgivingMiddleSegment_VoidStatement_LoweredWithoutResidual()
    {
        var result = Run(Source);

        var errors = string.Join("\n",
            result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(
            errors.Length == 0,
            "Diagnostics:\n" + string.Join("\n", result.Diagnostics)
            + "\n--- OUTPUT ---\n" + result.OutputCode);
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(0, result.Statistics.NullConditional.ResidualNodeCount);

        var code = System.Text.RegularExpressions.Regex.Replace(
            result.OutputCode, @"\s+", " ");
        // Receiver is still evaluated exactly once and the call is skipped on null.
        Assert.Contains("__ed_s0 = signatureNode;", code, StringComparison.Ordinal);
        Assert.Contains("__ed_s0 != null", code, StringComparison.Ordinal);
        Assert.Contains(
            "__ed_s0.Parent!.RemoveChild(signatureNode)",
            code, StringComparison.Ordinal);

        var tree = CSharpSyntaxTree.ParseText(
            result.OutputCode, new CSharpParseOptions(LanguageVersion.Preview));
        var recompiled = CSharpCompilation.Create(
            "c35-forgiving-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(recompiled.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
