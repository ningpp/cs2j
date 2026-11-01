using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Round 17 regression: a ref-like (ref struct) <c>ref</c>/<c>out</c> parameter
/// must stay NATIVE. <c>RefStructByRef&lt;T&gt;</c> is itself a ref struct and
/// cannot return a ref to its own field (CS8170); a ref field cannot point at a
/// ref struct (CS9050). A holder therefore degenerates into per-frame struct
/// copies (<c>Utf8JsonReader __shadowN = reader.Value;</c> + try/finally), which
/// multiplied the stack frame of dotnet.text.json's recursive reader until
/// <c>StreamTests.DeepNestedJsonFileTest</c> overflowed the default stack.
/// Keeping the original signature and call site is both semantically exact and
/// stack-minimal.
/// </summary>
public class RefOutInRefLikeNativeParamTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string Source = """
        public ref struct R
        {
            public int X;
            public void Advance() { X++; }
        }

        public static class Api
        {
            public static void Bump(ref R r) { r.Advance(); }
        }

        public static class Use
        {
            public static int Run()
            {
                R r = default;
                Api.Bump(ref r);
                return r.X;
            }
        }
        """;

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

    private static RefOutInRewriterResult Run(string src)
    {
        var tree = CSharpSyntaxTree.ParseText(src, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-ref-like-native-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Assembly AssertOutputCompilesAndEmits(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-ref-like-native-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors)
            + "\n===== OUTPUT =====\n" + code);

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
    }

    [Fact]
    public void RefLikeRef_Parameter_And_CallSite_StayNative()
    {
        var result = Run(Source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // Declaration stays native.
        Assert.Contains("static void Bump(ref R r)", result.OutputCode);
        // Call site stays native (no ByRef/RefStructByRef slot).
        Assert.Contains("Bump(ref r)", result.OutputCode);
        Assert.DoesNotContain("RefStructByRef", result.OutputCode);
        Assert.DoesNotContain("__shadow", result.OutputCode);
        Assert.DoesNotContain("__ref", result.OutputCode);
    }

    [Fact]
    public void RefLikeRef_Parameter_Runs_Without_Copies()
    {
        var result = Run(Source);

        var assembly = AssertOutputCompilesAndEmits(result.OutputCode);
        var run = assembly.GetType("Use")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal(1, run.Invoke(null, null));
    }
}
