using CSharpToJava.CLI;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.ExpressionDesugar.NullConditional;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.ExpressionDesugar;

/// <summary>
/// Regression for the GraphLayout corpus
/// (Test/MSAGLTests/Infrastructure/MsaglTestBase.cs and
/// Test/AglNingppTests/Agl/Routing/Support/MsaglTestBase.cs):
/// <code>
/// var baseDirectory = TestContext?.DeploymentDirectory;
/// baseDirectory = Path.Combine(TestContext?.TestRunDirectory ?? AppContext.BaseDirectory, "Out");
/// </code>
/// <c>TestContext</c> is declared in the file itself but its type lives in the
/// MSTest assembly, which is not part of the preprocessing compilation's
/// reference set. The receiver/terminal symbols therefore surface as error
/// types, and the old CS2J0559 guard preserved every site; the strict residual
/// gate then aborted the whole conversion. A single-segment member site needs
/// no type symbol at all: the hoisted temp is declared with <c>var</c> and the
/// null-check ternary is only ever rendered with a reference-typed receiver
/// (an unresolved type can never prove a <c>Nullable&lt;T&gt;</c> unwrap).
/// </summary>
public sealed class NullConditionalUnresolvedReceiverTests
{
    /// <summary>GraphLayout MsaglTestBase exact shape (both corpus copies).</summary>
    private const string MsaglTestBaseSource = """
        using System;
        using System.IO;
        using System.Linq;
        using Microsoft.VisualStudio.TestTools.UnitTesting;

        namespace Microsoft.Msagl.UnitTests {
            public class MsaglTestBase {
                public TestContext TestContext { get; set; }

                protected string GetDeploymentPath(params string[] relativePathSegments) {
                    if (relativePathSegments == null || relativePathSegments.Length == 0) {
                        throw new ArgumentException("A relative path is required.", nameof(relativePathSegments));
                    }

                    var baseDirectory = TestContext?.DeploymentDirectory;
                    if (string.IsNullOrEmpty(baseDirectory)) {
                        baseDirectory = Path.Combine(TestContext?.TestRunDirectory ?? AppContext.BaseDirectory, "Out");
                    }

                    return relativePathSegments.Aggregate(baseDirectory, Path.Combine);
                }

                protected string GetTestResourcePath(params string[] relativePathSegments) {
                    if (relativePathSegments == null || relativePathSegments.Length == 0) {
                        throw new ArgumentException("A resource path is required.", nameof(relativePathSegments));
                    }

                    var resourcePath = Path.Combine(new[] { "Resources" }.Concat(relativePathSegments).ToArray());
                    var deploymentDirectory = TestContext?.DeploymentDirectory;
                    if (!string.IsNullOrEmpty(deploymentDirectory)) {
                        var deploymentPath = Path.Combine(deploymentDirectory, resourcePath);
                        if (Directory.Exists(deploymentPath) || File.Exists(deploymentPath)) {
                            return deploymentPath;
                        }
                    }

                    var testRunDirectory = TestContext?.TestRunDirectory;
                    if (!string.IsNullOrEmpty(testRunDirectory)) {
                        var testRunPath = Path.Combine(testRunDirectory, "Out", resourcePath);
                        if (Directory.Exists(testRunPath) || File.Exists(testRunPath)) {
                            return testRunPath;
                        }
                    }

                    return Path.Combine(AppContext.BaseDirectory, resourcePath);
                }

                string ResolveTestFilePath(string filePath) {
                    if (Path.IsPathRooted(filePath) || File.Exists(filePath)) {
                        return filePath;
                    }

                    var baseDirectories = new[] {
                        TestContext?.DeploymentDirectory,
                        string.IsNullOrEmpty(TestContext?.TestRunDirectory) ? null : Path.Combine(TestContext.TestRunDirectory, "Out"),
                        AppContext.BaseDirectory,
                    };

                    foreach (var baseDirectory in baseDirectories.Where(directory => !string.IsNullOrEmpty(directory))) {
                        var directPath = Path.Combine(baseDirectory, filePath);
                        if (File.Exists(directPath)) {
                            return directPath;
                        }
                    }

                    return filePath;
                }
            }
        }
        """;

    private static ExpressionDesugarResult PreprocessWithBasicReferences(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "null-conditional-basic-references",
            new[] { tree },
            ProjectUnsignedTypePreprocessor.GetBasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new ExpressionDesugarPreprocessor().Preprocess(
            source, model,
            new ExpressionDesugarOptions
            {
                NullConditional = new NullConditionalOptions { Strict = true },
            });
    }

    /// <summary>
    /// <see cref="ProjectUnsignedTypePreprocessor.GetBasicReferences"/> also lists
    /// the native WindowsDesktop payload on this runtime; only the managed
    /// assemblies are usable as compilation references.
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
    public void ConditionalAccess_Receiver_Type_From_Unreferenced_Assembly_Is_Rewritten()
    {
        var result = PreprocessWithBasicReferences(MsaglTestBaseSource);

        var errors = string.Join(
            "\n", result.Diagnostics.Where(d => d.Severity == ExpressionDesugarSeverity.Error));
        Assert.True(result.Changed, "file should be rewritten. Diagnostics:\n" + errors);
        Assert.Equal(string.Empty, errors);
        Assert.DoesNotContain("CS2J0559", string.Join(
            "\n", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));

        // Every ?. must be explicitized; no residual trigger node may survive.
        Assert.DoesNotContain("?.", result.OutputCode, StringComparison.Ordinal);
        Assert.Equal(0, result.Statistics.NullConditional.ResidualNodeCount);
        Assert.Equal(0, result.Statistics.ResidualCount);

        // The temp-declaration form needs no type symbol; the unresolved type
        // also proves no Nullable<T> unwrap, so the reference-typed form is the
        // only valid rendering.
        Assert.Contains(
            "__ed_s0 != null ? __ed_s0.DeploymentDirectory : null",
            result.OutputCode);
        Assert.Contains(
            "__ed_s1 != null ? __ed_s1.TestRunDirectory : null",
            result.OutputCode);
        Assert.DoesNotContain("__ed_s0.Value", result.OutputCode);
    }

    [Fact]
    public void ConditionalAccess_Unresolved_Type_Output_Compiles_Once_The_Type_Is_Available()
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
            "null-conditional-unresolved-output",
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
