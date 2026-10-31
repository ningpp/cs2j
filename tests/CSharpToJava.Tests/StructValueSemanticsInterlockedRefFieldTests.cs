using System.IO;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonDocument.DbRow.cs</c>:
/// <c>Interlocked.Exchange(ref _data, null!)</c> inside the
/// <c>MetadataDb.Dispose()</c> struct method. Interlocked only performs a
/// one-shot atomic read/modify/write and never retains the reference, so the
/// mutable-field ref argument is not a clone-boundary escape. It used to be
/// reported as SV040 and failed the whole struct-value-semantics pass.
/// </summary>
public class StructValueSemanticsInterlockedRefFieldTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private const string Source = """
        using System.Threading;

        namespace Lib
        {
            public struct Box
            {
                private byte[] _data;
                public int Length { get; private set; }

                public void Reset()
                {
                    Interlocked.Exchange(ref _data, null!);
                }
            }
        }
        """;

    [Fact]
    public void InterlockedExchange_OnMutableField_IsNotSV040()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "svs-interlocked-" + Guid.NewGuid().ToString("N"),
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
            d => d.Code == StructValueSemanticsDiagnosticCode.SV040);
        Assert.DoesNotContain(diagnostics,
            d => d.Severity == StructValueSemanticsSeverity.Error);
        Assert.Contains("Clone", result.OutputCode, StringComparison.Ordinal);

        var verify = CSharpCompilation.Create(
            "svs-interlocked-emit-" + Guid.NewGuid().ToString("N"),
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
