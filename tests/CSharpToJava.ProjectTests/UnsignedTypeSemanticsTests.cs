using System.Reflection;
using CSharpToJava.CLI;
using CSharpToJava.Core.UnsignedTypeRewriter;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

public class UnsignedTypeSemanticsTests
{
    private static string LibDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleUnsignedSolution", "SampleUnsignedLib");

    [Fact]
    public void RewrittenCode_ProducesSameRuntimeResults_AsOriginal()
    {
        Assert.True(Directory.Exists(LibDir), $"Fixture not found: {LibDir}");

        var files = Directory.GetFiles(LibDir, "*.cs")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(files);

        var sources = files
            .Select(f => (Name: Path.GetFileName(f), Code: File.ReadAllText(f)))
            .ToList();
        var trees = sources
            .Select(s => CSharpSyntaxTree.ParseText(s.Code, path: s.Name))
            .ToList();
        var references = GetCoreReferences();
        var compilation = CSharpCompilation.Create(
            "unsigned-semantics-original",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var rewriter = new UnsignedTypeRewriter();
        var rewrittenSources = new List<string>();
        foreach (var (name, code) in sources)
        {
            var tree = trees.First(t => t.FilePath == name);
            var result = rewriter.Rewrite(code, compilation.GetSemanticModel(tree));
            rewrittenSources.Add(result.OutputCode);
        }

        rewrittenSources.Add(UnsignedMathFileContent.Generate());

        var originalOutput = CompileAndRun(sources.Select(s => s.Code), references, "unsigned-semantics-original");
        var rewrittenOutput = CompileAndRun(rewrittenSources, references, "unsigned-semantics-rewritten");

        Assert.Equal(originalOutput, rewrittenOutput);
    }

    private static string CompileAndRun(
        IEnumerable<string> sources,
        IEnumerable<MetadataReference> references,
        string assemblyName)
    {
        var trees = sources
            .Select((s, index) => CSharpSyntaxTree.ParseText(
                s,
                path: $"{assemblyName}-{index}.cs"))
            .ToList();
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        var errorText = string.Join(
            "\n",
            emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
        var firstError = emit.Diagnostics
            .FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        var errorSource = firstError?.Location.SourceTree != null
            ? "\n--- source ---\n" + firstError.Location.SourceTree.GetText().ToString()
            : string.Empty;
        Assert.True(emit.Success, errorText + errorSource);

        var assembly = Assembly.Load(stream.ToArray());
        var type = assembly.GetType("SampleUnsignedLib.UnsignedSemantics", throwOnError: true)!;
        var method = type.GetMethod("RunAll", BindingFlags.Public | BindingFlags.Static)!;
        return (string)method.Invoke(null, null)!;
    }

    private static List<MetadataReference> GetCoreReferences()
    {
        var references = new List<MetadataReference>();
        var trustedAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedAssemblies == null)
        {
            references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
            return references;
        }

        foreach (var assembly in trustedAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(assembly))
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        return references;
    }
}
