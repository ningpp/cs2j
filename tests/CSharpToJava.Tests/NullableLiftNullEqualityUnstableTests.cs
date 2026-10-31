using System.IO;
using CSharpToJava.Core.NumericBoundary;
using CSharpToJava.Core.NumericBoundary.Rules.Nullable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonPropertyInfoOfT.cs</c>:
/// <c>else if (Converter.CanUseDirectReadOrWrite &amp;&amp;
/// state.Value.Current.NumberHandling == null)</c>. The nullable side is an
/// unstable property chain inside a short-circuit RHS (un-hoistable), which
/// used to fail the whole numeric-boundary pass with CS2J5355. The
/// null-literal equality expands to a single <c>!x.HasValue</c> read, so it
/// needs no temp at all.
/// </summary>
public class NullableLiftNullEqualityUnstableTests
{
    private const string Source = """
        using System;

        class C
        {
            class State
            {
                public Frame Current { get; set; } = new Frame();
            }

            class Frame
            {
                public int? NumberHandling { get; set; }
            }

            bool M(State state, bool x, bool y)
            {
                if (x)
                {
                    return true;
                }
                else if (y && state.Current.NumberHandling == null)
                {
                    return true;
                }

                return false;
            }
        }
        """;

    [Fact]
    public void NullEquality_UnstablePropertyChain_InShortCircuit_NeedsNoHoist()
    {
        var tree = (SyntaxTree)CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "nl-null-eq-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);

        var compileErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(compileErrors.Count == 0,
            "test input failed to compile:\n" + string.Join("\n", compileErrors));

        var diagnostics = new List<NumericBoundaryDiagnostic>();
        var context = new NumericBoundaryRuleContext(
            (CompilationUnitSyntax)tree.GetRoot(),
            model,
            projectDefaultChecked: false,
            new NumericBoundaryOptions(),
            new NumericBoundaryStatistics(),
            diagnostics);
        var result = new NullableLiftRule().Execute(context);

        Assert.DoesNotContain(diagnostics,
            d => d.Severity == NumericBoundarySeverity.Error);
        Assert.True(result.MadeChanges);

        var output = context.Root.ToFullString();
        Assert.Contains("HasValue", output);
        Assert.DoesNotContain("__cs2j_n_", output);

        var verify = CSharpCompilation.Create(
            "nl-null-eq-emit-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(output) },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = verify.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Rewritten code failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + output);
    }

    private static IEnumerable<MetadataReference> GetReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted != null)
        {
            foreach (var assembly in trusted.Split(Path.PathSeparator))
            {
                if (File.Exists(assembly))
                {
                    yield return MetadataReference.CreateFromFile(assembly);
                }
            }

            yield break;
        }

        yield return MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
    }
}
