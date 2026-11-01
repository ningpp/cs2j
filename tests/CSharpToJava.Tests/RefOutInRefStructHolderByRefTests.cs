using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Round 17: a ref-like (ref struct) <c>ref</c>/<c>out</c> parameter keeps its
/// NATIVE form. <c>RefStructByRef&lt;T&gt;</c> cannot return a ref to its own
/// field (CS8170) and a ref field cannot point at a ref struct (CS9050), so a
/// holder wrapper would have to copy the struct on every call/return — one
/// shadow per recursive frame, which is what blew the default stack in
/// dotnet.text.json's deep-nested reader. The correct intermediate form is the
/// original signature (<c>Bump(ref Reader r)</c>) plus the untouched call site.
/// </summary>
public class RefOutInRefStructHolderByRefTests
{
    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
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

    private const string RefWriterSource = """
        public ref struct Reader
        {
            public int Pos;
            public void Advance() { Pos++; }
        }

        public static class Api
        {
            public static void Bump(ref Reader r) { r.Advance(); }

            public static int Run()
            {
                Reader r = default;
                Api.Bump(ref r);
                return r.Pos;
            }
        }
        """;

    private const string OutRefStructSource = """
        public ref struct Reader
        {
            public int Pos;
            public void Advance() { Pos++; }
        }

        public static class Api
        {
            public static void Fill(out Reader r) { r = default; r.Pos = 7; }

            public static int Run()
            {
                Fill(out Reader r);
                return r.Pos;
            }
        }
        """;

    private const string ReadOnlySource = """
        public ref struct Reader
        {
            public int Pos;
            public void Advance() { Pos++; }
        }

        public static class Api
        {
            public static int Peek(ref Reader r) => r.Pos;

            public static int Run()
            {
                Reader r = default;
                r.Pos = 5;
                return Api.Peek(ref r);
            }
        }
        """;

    /// <summary>
    /// A span returned from a ref-struct holder must keep its escape scope:
    /// `RefStructByRef&lt;Reader&gt; __ref = Create(…); GetSpan(ref __ref)`
    /// would give the span method-scope safe-to-escape and the `return`
    /// becomes CS8352/CS8347 (dotnet.text.json GetPropertyName).
    /// </summary>
    private const string SpanReturningSource = """
        public ref struct Reader
        {
            public int Pos;
            public System.ReadOnlySpan<byte> Data;
            public void Advance() { Pos++; }
        }

        internal static class Ext
        {
            public static System.ReadOnlySpan<byte> GetData(this ref Reader r) => r.Data;
        }

        public static class Api
        {
            public static System.ReadOnlySpan<byte> Wrap(System.ReadOnlySpan<byte> src, int i) => src;

            public static System.ReadOnlySpan<byte> Name(ref Reader r)
            {
                System.ReadOnlySpan<byte> name = r.GetData();
                r.Advance();
                return Wrap(name, 0);
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
            "roi-refstruct-byref-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Compilation Verify(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        return CSharpCompilation.Create(
            "roi-refstruct-byref-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static Compilation AssertOutputCompiles(string code)
    {
        var compilation = Verify(code);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors)
            + "\n===== OUTPUT =====\n" + code);
        return compilation;
    }

    private static Assembly LoadAssembly(Compilation compilation)
    {
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
    }

    private static int InvokeRun(Assembly assembly) =>
        (int)assembly.GetType("Api")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;

    private static RefOutInRewriterResult RewriteAndAssertClean(string src)
    {
        var result = Run(src);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        return result;
    }

    [Fact]
    public void RefLikeRef_Parameter_StaysNative()
    {
        var result = RewriteAndAssertClean(RefWriterSource);

        // (A) the ref-like parameter keeps its native `ref Reader` form.
        Assert.Matches(new Regex(@"static void Bump\(ref Reader r\)"),
            result.OutputCode);
        // (A) the call site keeps its native `ref`.
        Assert.Matches(new Regex(@"Bump\(ref r\)"), result.OutputCode);
        Assert.DoesNotContain("RefStructByRef", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        Assert.Equal(1, InvokeRun(LoadAssembly(compilation)));
    }

    [Fact]
    public void RefLikeRef_BodyMutation_NeedsNoShadow()
    {
        var result = RewriteAndAssertClean(RefWriterSource);

        // (B) native `ref` aliases the caller's struct: no copy-in/copy-out.
        Assert.DoesNotContain("__shadow", result.OutputCode);
        Assert.DoesNotContain(".Value", result.OutputCode);
        Assert.Matches(new Regex(@"r\.Advance\(\);"), result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(1, InvokeRun(assembly));
    }

    [Fact]
    public void RefLikeOut_Parameter_StaysNative()
    {
        var result = RewriteAndAssertClean(OutRefStructSource);

        Assert.Matches(new Regex(@"static void Fill\(out Reader r\)"),
            result.OutputCode);
        Assert.Matches(new Regex(@"Fill\(out Reader r\)"), result.OutputCode);
        Assert.DoesNotContain("RefStructByRef", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(7, InvokeRun(assembly));
    }

    [Fact]
    public void RefLikeRef_ReadOnlyBody_NeedsNoShadow()
    {
        var result = RewriteAndAssertClean(ReadOnlySource);

        Assert.Matches(new Regex(@"static int Peek\(ref Reader r\)"),
            result.OutputCode);
        // A read-only body binds straight to the native parameter: no shadow.
        Assert.DoesNotContain("__shadow", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(5, InvokeRun(assembly));
    }

    [Fact]
    public void RefLikeThisRef_Extension_StaysNative()
    {
        var result = RewriteAndAssertClean(SpanReturningSource);

        // A ref-like `this ref` extension is NOT converted to a holder: the
        // reduced extension call keeps its native receiver and drop-in scope.
        Assert.Matches(new Regex(@"GetData\(this ref Reader r\)"),
            result.OutputCode);
        Assert.Matches(new Regex(@"r\.GetData\(\)"), result.OutputCode);
        Assert.DoesNotContain("RefStructByRef", result.OutputCode);
        Assert.DoesNotContain("global::Ext.GetData", result.OutputCode);
        // A span return value is never materialized into a `__ret` local.
        Assert.DoesNotContain("__ret", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }
}
