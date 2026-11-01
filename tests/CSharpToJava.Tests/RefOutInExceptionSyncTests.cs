using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for exception-path holder synchronization in the ref/out/in pass.
/// A callee that mutates a <c>ref</c> argument and then throws used to lose the
/// mutation: the shadow copy-out in the callee only ran on a normal return, and
/// the caller's read-back was emitted after the call (skipped by the exception).
/// dotnet.text.json's <c>JsonDocument.Parse</c> then disposed a
/// <c>MetadataDb</c> still holding the already-returned old <c>_data</c> array
/// (double RETURN of the same <c>byte[256]</c>), which let a later
/// <c>ArrayPool</c> caller share and clear the failing test's buffer
/// (<c>HonorReaderOptionsMaxDepth</c>).
/// </summary>
public class RefOutInExceptionSyncTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
            }
            public ref struct RefStructByRef<T> where T : allows ref struct
            {
                private T _slot;
                public RefStructByRef() { _slot = default!; }
                private RefStructByRef(T initial) { _slot = initial; }
                public static RefStructByRef<T> Create() => new();
                public static RefStructByRef<T> Create(T initial) => new(initial);
                public T Value { get => _slot; set => _slot = value; }
            }
        }
        """;

    private const string StructThrowSource = """
        public struct Box { public int X; }

        public static class Api
        {
            public static void MutateAndThrow(ref Box b)
            {
                b.X = 1;
                throw new System.Exception("boom");
            }

            public static void Mutate(ref Box b) { b.X = 7; }
        }

        public static class Use
        {
            public static int RunThrow()
            {
                Box b = default;
                try { Api.MutateAndThrow(ref b); } catch { }
                return b.X;
            }

            public static int RunNormal()
            {
                Box b = default;
                Api.Mutate(ref b);
                return b.X;
            }
        }
        """;

    private const string RefStructThrowSource = """
        public ref struct R
        {
            public int X;
            public void Set(int v) { X = v; }
        }

        public static class Api
        {
            public static void MutateAndThrow(ref R r)
            {
                r.Set(1);
                throw new System.Exception("boom");
            }
        }

        public static class Use
        {
            public static int Run()
            {
                R r = default;
                try { Api.MutateAndThrow(ref r); } catch { }
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
            "roi-exception-sync-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Assembly AssertOutputCompilesAndEmits(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-exception-sync-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
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

    private static int InvokeRun(Assembly assembly, string method) =>
        (int)assembly.GetType("Use")!
            .GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;

    [Fact]
    public void RefStruct_Holder_Mutation_Survives_Exception()
    {
        var result = Run(StructThrowSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        Assert.Equal(1, InvokeRun(asm, "RunThrow"));
        // A normal return keeps the same copy-back contract.
        Assert.Equal(7, InvokeRun(asm, "RunNormal"));
    }

    [Fact]
    public void Lifted_Ref_Readback_Runs_In_Finally()
    {
        var result = Run(StructThrowSource);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The callee's shadow copy-out and the caller's read-back both live in
        // a finally block, so the mutation is visible after the throw.
        Assert.Contains("finally", result.OutputCode);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"finally\s*\{\s*b = __ref\d+\.Value;"), result.OutputCode);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        Assert.Equal(1, InvokeRun(asm, "RunThrow"));
    }

    [Fact]
    public void RefStruct_ByRef_Holder_Mutation_Survives_Exception()
    {
        var result = Run(RefStructThrowSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        Assert.Equal(1, InvokeRun(asm, "Run"));
    }
}
