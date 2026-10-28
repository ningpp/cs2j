using CSharpToJava.CLI;
using CSharpToJava.Core.NullCoalesceRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// Regression for the GraphLayout corpus (Test/MSAGLTests/Infrastructure/MsaglTestBase.cs
/// and Test/AglNingppTests/Agl/Routing/Support/MsaglTestBase.cs):
/// <code>
/// baseDirectory = Path.Combine(TestContext?.TestRunDirectory ?? AppContext.BaseDirectory, "Out");
/// </code>
/// The ?? left operand is typed by the MSTest <c>TestContext</c> property, whose
/// assembly is not part of the preprocessing compilation's reference set. The
/// rewrite only needs that symbol to decide whether the left operand is a
/// Nullable&lt;T&gt; (the <c>___t.Value</c> unwrap); the hoisted temp is declared
/// with <c>var</c>, so an unresolved type is no reason to leave the operator
/// behind — the previous behavior skipped the rewrite, kept the ?? and the strict
/// residual gate then aborted the whole conversion.
/// </summary>
public sealed class NullCoalesceUnresolvedLeftTypeTests
{
    /// <summary>GraphLayout MsaglTestBase.GetDeploymentPath exact shape.</summary>
    private const string MsaglTestBaseSource = """
        using System;
        using System.IO;
        using System.Linq;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Microsoft.Msagl.UnitTests {
            public class MsaglTestBase {
                public TestContext TestContext { get; set; }

                protected string GetDeploymentPath(params string[] relativePathSegments) {
                    var baseDirectory = TestContext?.DeploymentDirectory;
                    if (string.IsNullOrEmpty(baseDirectory)) {
                        baseDirectory = Path.Combine(TestContext?.TestRunDirectory ?? AppContext.BaseDirectory, "Out");
                    }

                    return relativePathSegments.Aggregate(baseDirectory, Path.Combine);
                }
            }
        }
        """;

    /// <summary>
    /// Same reference set the CLI builds the pass compilation from: the runtime's
    /// trusted platform assemblies only, so MSTest types stay unresolved exactly
    /// like they are on the GraphLayout run.
    /// </summary>
    private static NullCoalesceResult PreprocessWithBasicReferences(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "null-coalesce-basic-references",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new NullCoalesceRewriter().Preprocess(source, model);
    }

    /// <summary>
    /// <see cref="ProjectUnsignedTypePreprocessor.GetBasicReferences"/> also lists
    /// the native WindowsDesktop payload (wpfgfx_cor3.dll, ...) on this runtime;
    /// only the managed assemblies are usable as compilation references.
    /// </summary>
    private static List<MetadataReference> ManagedBasicReferences()
    {
        var references = new List<MetadataReference>();
        foreach (var reference in ProjectUnsignedTypePreprocessor.GetBasicReferences())
        {
            var path = (reference as PortableExecutableReference)?.FilePath;
            if (path == null)
            {
                continue;
            }

            try
            {
                System.Reflection.AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                continue;
            }

            references.Add(reference);
        }

        return references;
    }

    [Fact]
    public void Coalesce_LeftOperand_Type_From_Unreferenced_Assembly_Is_Rewritten()
    {
        var result = PreprocessWithBasicReferences(MsaglTestBaseSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain("??", result.OutputCode);

        // The temp-declaration form needs no type symbol at all.
        Assert.Contains("var ___t1 = TestContext?.TestRunDirectory;", result.OutputCode);
        Assert.Contains("___t1 != null ? ___t1 : AppContext.BaseDirectory", result.OutputCode);

        // No nullable-value unwrap can be proven, so the reference-typed form
        // (no .Value) is the only valid guess.
        Assert.DoesNotContain("___t1.Value", result.OutputCode);

        Assert.Equal(0, result.Statistics.ResidualCount);
        Assert.Equal(0, result.Statistics.SkippedUnsupported);
        Assert.Equal(1, result.Statistics.NullCoalescingRewritten);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Coalesce_Unresolved_Type_Output_Compiles_Once_The_Type_Is_Available()
    {
        // The real project build does have the MSTest reference, so the rewritten
        // code must compile there: replay the output against a stub TestContext.
        var result = PreprocessWithBasicReferences(MsaglTestBaseSource);
        var outputTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var stubTree = CSharpSyntaxTree.ParseText("""
            namespace Microsoft.VisualStudio.TestTools.UnitTesting {
                public class TestContext {
                    public string? TestRunDirectory { get; set; }
                    public string? DeploymentDirectory { get; set; }
                }
            }
            """);
        var compilation = CSharpCompilation.Create(
            "null-coalesce-unresolved-output",
            new[] { outputTree, stubTree },
            ManagedBasicReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.Empty(errors);
    }
}
