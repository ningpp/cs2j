using CSharpToJava.Core.DefaultParameterStripper;
using CSharpToJava.Core.DefaultParameterStripper.ParamsCollections;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C48 regression: a comma-form call to a metadata/framework params collection
/// (e.g. .NET 10 <c>string.Concat(params ReadOnlySpan&lt;object?&gt;)</c>,
/// <c>System.IO.Path.Combine(params ReadOnlySpan&lt;string&gt;)</c>) has no
/// source declaration plan because only source declarations are stripped. The
/// call already binds legally against the untouched metadata signature, so it
/// must pass through as info (CS2J0614), never as the strict error CS2J0602.
/// </summary>
public class ParamsMetadataPassthroughTests
{
    private static ParamsCollectionsOptions StrictOn()
        => new(Enabled: true, Strict: true);

    private static (DefaultParameterStripperResult Result, ParamsStatistics? Stats) Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "c48-metadata-" + System.Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

        var options = new DefaultParameterStripperOptions(ParamsCollections: StrictOn());
        var stripper = new DefaultParameterStripper();
        stripper.Collect(model.SyntaxTree, model, options);
        var result = stripper.Strip(src, model, options);
        return (result, stripper.ParamsResult?.Statistics);
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

    [Fact]
    public void Bcl_Params_Span_Call_Passes_Through_Without_Resolution_Error()
    {
        // Path.Combine only ships fixed 2/3/4-string overloads plus the .NET 10
        // params ReadOnlySpan<string?> member: five segments can ONLY bind to
        // the metadata params span (a ParamCollection expansion whose target
        // cs2j never collects, since only source declarations are planned).
        // Fixed-arity calls (3 args) bind to the normal string overload and
        // never reach the params machinery at all.
        var src = """
            using System.IO;
            class C
            {
                static string Span() => Path.Combine("a", "b", "c", "d", "e");
                static string Fixed() => Path.Combine("a", "b", "c");
            }
            """;

        var (result, stats) = Run(src);

        // No hard resolution failure even under strict mode; the benign
        // passthrough is recorded in statistics (info codes are stats-only).
        Assert.DoesNotContain(result.Diagnostics, d => d.Message.StartsWith("CS2J0602"));
        Assert.NotNull(stats);
        Assert.True(stats!.ParamsCallSitesPassthrough >= 1);
        Assert.True(stats.SkippedByReason.TryGetValue(ParamsDiagnostics.MetadataCollectionPassthrough, out var metaCount)
            && metaCount >= 1);

        // Nothing to rewrite: metadata signatures are untouched, so the file
        // stays byte-identical and still compiles.
        Assert.False(result.Changed);
        Assert.Equal(src, result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create(
            "c48-metadata-verify",
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "passthrough output failed to compile:\n" + string.Join("\n", errors));
    }
}
