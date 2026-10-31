using System.IO;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>Stream.ReadTests.cs</c>:
/// <c>Assert.False(e.EnumerateObject().MoveNext());</c> — a mutating struct
/// method call nested in an expression statement argument. Previously this was
/// reported as SV043 (no hoist) and failed the whole struct-value-semantics
/// pass. The defensive-copy temp can be hoisted in front of the enclosing
/// statement, so the site is rewritten instead:
/// <c>var __cs2jVc0 = e.Clone(); Assert.False(__cs2jVc0.MoveNext());</c>.
/// </summary>
public class StructValueSemanticsNestedReceiverHoistTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string Source = """
        namespace Lib;

        public struct Point
        {
            public int X;

            public int Bump()
            {
                X++;
                return X;
            }

            private static readonly Point Origin = new();

            public static void Use()
            {
                AssertFalse(Origin.Bump());
            }

            private static void AssertFalse(int value) { }
        }
        """;

    [Fact]
    public void MutatingCallNestedInExpressionStatementArgument_IsHoistedNotSV043()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "svs-nested-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var preprocessor = new StructValueSemanticsPreprocessor(
            new StructValueSemanticsOptions());
        preprocessor.BindCompilation(compilation);

        var statistics = new StructValueSemanticsStatistics();
        var diagnostics = new List<StructValueSemanticsDiagnostic>();
        var result = preprocessor.RewriteFile(
            Source, compilation.GetSemanticModel(tree), statistics, diagnostics);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Contains("__cs2jVc", result.OutputCode, StringComparison.Ordinal);

        var verify = CSharpCompilation.Create(
            "svs-nested-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + result.OutputCode);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly)) yield return MetadataReference.CreateFromFile(assembly);
            }
            yield break;
        }
        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
