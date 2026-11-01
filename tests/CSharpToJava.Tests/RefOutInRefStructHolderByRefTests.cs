using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: a ref-struct holder
/// (<c>Cs2jByRef.RefStructByRef&lt;T&gt;</c>, itself a ref struct) is a
/// <c>ref struct</c>, so passing it BY VALUE copies its <c>_slot</c> field:
/// every write the callee performs (<c>reader.Value = __shadow</c>, the
/// <c>Value.Read()</c> mutation) stays in that copy and the caller keeps the
/// stale reader. The emitted serializer read loop therefore never advanced
/// (dotnet.text.json: "The JSON value could not be converted … Path: $",
/// <c>ContinuationTests.ContinuationAtNullToken</c>).
/// <para>
/// Two halves are exercised here:
/// (A) the holder parameter is <c>ref Cs2jByRef.RefStructByRef&lt;T&gt;</c> and
///     every lifted call site passes <c>ref</c>;
/// (B) the holder body gets a copy-in/copy-out shadow local
///     (<c>T __shadowN = holder.Value; …; holder.Value = __shadowN;</c>) so
///     member mutations land in the holder slot instead of a discarded
///     property-return copy.
/// </para>
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
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
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
    public void RefStructHolder_Parameter_IsPassedByRef()
    {
        var result = RewriteAndAssertClean(RefWriterSource);

        // (A) the holder parameter keeps reference semantics.
        Assert.Matches(new Regex(
            @"static void Bump\(ref Cs2jByRef\.RefStructByRef<Reader> r\)"),
            result.OutputCode);
        // (A) every call site adds `ref`.
        Assert.Matches(new Regex(@"Bump\(ref [A-Za-z_][A-Za-z0-9_]*\)"), result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RefStructHolder_BodyMutation_UsesShadowCopyOut()
    {
        var result = RewriteAndAssertClean(RefWriterSource);

        // (B) the mutation is applied to a shadow local that is written back
        // into the holder slot before returning.
        Assert.Matches(new Regex(@"Reader __shadow\d+ = r\.Value;"), result.OutputCode);
        Assert.Matches(new Regex(@"__shadow\d+\.Advance\(\);"), result.OutputCode);
        Assert.Matches(new Regex(@"r\.Value = __shadow\d+;"), result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(1, InvokeRun(assembly));
    }

    [Fact]
    public void RefStructHolder_OutParameter_WritesBackThroughHolder()
    {
        var result = RewriteAndAssertClean(OutRefStructSource);

        Assert.Matches(new Regex(
            @"static void Fill\(ref Cs2jByRef\.RefStructByRef<Reader> r\)"),
            result.OutputCode);
        Assert.Matches(new Regex(@"Fill\(ref [A-Za-z_][A-Za-z0-9_]*\)"), result.OutputCode);
        Assert.Matches(new Regex(@"r\.Value = __shadow\d+;"), result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(7, InvokeRun(assembly));
    }

    [Fact]
    public void RefStructHolder_ReadOnlyBody_NeedsNoShadow()
    {
        var result = RewriteAndAssertClean(ReadOnlySource);

        Assert.Matches(new Regex(
            @"static int Peek\(ref Cs2jByRef\.RefStructByRef<Reader> r\)"),
            result.OutputCode);
        // A read-only body binds straight to the holder value: no shadow frame.
        Assert.DoesNotContain("__shadow", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal(5, InvokeRun(assembly));
    }

    [Fact]
    public void RefStructHolder_SpanReturningCall_KeepsEscapeScope()
    {
        var result = RewriteAndAssertClean(SpanReturningSource);

        // The shadowed holder is flushed into its slot and passed BY REFERENCE:
        // no `RefStructByRef<T>` local may sit between the holder and the span
        // (that would give the span method-scope safe-to-escape → CS8352).
        Assert.Matches(new Regex(@"GetData\(ref r\)"), result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"__ref\d+ = RefStructByRef<Reader>\.Create"), result.OutputCode);
        // A span return value is never materialized into a `__ret` local.
        Assert.DoesNotContain("__ret", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }
}
