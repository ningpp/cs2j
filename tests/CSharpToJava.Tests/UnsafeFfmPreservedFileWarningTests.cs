using System.IO;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.UnsafeFfm;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json unsafe-ffm rejections (span-backed fixed pointers,
/// stackalloc-to-bare-pointer, IntPtr properties). A rejected file is
/// preserved byte-for-byte, so the C# intermediate tree stays valid; the CLI
/// adapter now downgrades those rollback diagnostics to warnings instead of
/// aborting the whole preprocess chain. Internal rule-gap errors (UF010)
/// stay fatal.
/// </summary>
public class UnsafeFfmPreservedFileWarningTests
{
    private const string Source = """
        using System;

        unsafe class C
        {
            static int M(ReadOnlySpan<char> value)
            {
                fixed (char* ptr = value)
                {
                    return 1;
                }
            }
        }
        """;

    [Fact]
    public async Task RejectedFile_IsPreserved_AndDoesNotFailThePass()
    {
        var tree = CSharpSyntaxTree.ParseText(
            Source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "uf-preserved-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var adapter = new UnsafeFfmPassAdapter(
            new UnsafeFfmOptions(),
            verbose: false,
            filterUnsupportedProjects: false,
            compatNamespacePrefixes: null,
            verifyBuild: false,
            verifyTests: false);

        var result = await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = Source,
            SourceFilePath = "C.cs",
            RelativePath = "C.cs",
            DestinationFilePath = "C.cs",
            SemanticModel = compilation.GetSemanticModel(tree),
        });

        Assert.Null(result);
        Assert.False(adapter.HasErrors);
        Assert.Contains(adapter.Diagnostics, d =>
            d.Code == UnsafeFfmDiagnosticCode.UF020
            && d.Severity == UnsafeFfmSeverity.Warning);
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
