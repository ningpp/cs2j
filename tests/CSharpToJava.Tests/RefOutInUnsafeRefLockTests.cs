using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression (dotnet.text.json, ~850 failures): a <c>ref</c>/<c>out</c>
/// parameter on an <c>unsafe</c> method is a memory ADDRESS, not a
/// <c>ByRef&lt;T&gt;</c> value slot. The body walks it with pointer arithmetic
/// (<c>Unsafe.AsPointer(ref searchSpace)</c>, <c>Unsafe.AddByteOffset(ref
/// searchSpace, n)</c>), so copy-in/copy-out lifting scanned a stack COPY of
/// the referenced byte and <c>IndexOfOrLessThan</c> returned a wrong index —
/// <c>JsonReaderHelper.IndexOfQuoteOrAnyControlOrBackSlash</c> then made
/// <c>Unescape</c> throw and every JSON document test failed.
/// <para>
/// The pass must therefore treat <c>unsafe</c> methods with ref/out params as
/// locked: keep the native signature and the native call sites (the address
/// form belongs to the deferred unsafe/FFM domain), while ordinary methods
/// keep being rewritten to <c>ByRef&lt;T&gt;</c>.
/// </para>
/// </summary>
public class RefOutInUnsafeRefLockTests
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
                public static ByRef<T> Create(System.Func<T> getter, System.Action<T> setter)
                    => new AccessorByRef<T>(getter, setter);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
            }
            internal sealed class AccessorByRef<T> : ByRef<T>
            {
                private readonly System.Func<T> _getter;
                private readonly System.Action<T> _setter;
                public AccessorByRef(System.Func<T> getter, System.Action<T> setter)
                { _getter = getter; _setter = setter; }
                public override T Value { get => _getter(); set => _setter(value); }
            }
        }
        """;

    /// <summary>Unsafe callee called with a plain local `ref b`.</summary>
    private const string UnsafeLocalSource = """
        using System;

        public static unsafe class Api
        {
            public static int Scan(ref byte searchSpace)
            {
                return *((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref searchSpace));
            }
        }

        public static class Use
        {
            public static byte Run()
            {
                byte b = 7;
                System.Span<byte> span = new byte[] { 1, 2, 3 }.AsSpan();
                Api.Scan(ref b);
                return b;
            }
        }
        """;

    /// <summary>Unsafe callee called with a ref-returning invocation argument.</summary>
    private const string UnsafeRefReturnSource = """
        using System;

        public static unsafe class Api
        {
            public static int IndexOfLessThan(ref byte searchSpace, byte lessThan, int length)
            {
                byte* p = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref searchSpace);
                for (int i = 0; i < length; i++)
                {
                    if (p[i] < lessThan) return i;
                }
                return -1;
            }
        }

        public static class Use
        {
            public static int Run()
            {
                System.Span<byte> span = new byte[] { 100, 100, 5, 100 }.AsSpan();
                return Api.IndexOfLessThan(
                    ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span), 32, span.Length);
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
            "roi-unsafe-ref-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static (Compilation Compilation, List<Diagnostic> Errors) Verify(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-unsafe-ref-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return (compilation, errors);
    }

    private static Compilation AssertOutputCompiles(string code)
    {
        var (compilation, errors) = Verify(code);
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

    private static object InvokeRun(Assembly assembly, string type = "Use") =>
        assembly.GetType(type)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;

    [Fact]
    public void Unsafe_Ref_Parameter_Keeps_Native_Ref_And_Call_Site()
    {
        var result = Run(UnsafeLocalSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The unsafe callee keeps its native ref parameter ...
        Assert.Matches(new Regex(@"static int Scan\(ref byte searchSpace\)"), result.OutputCode);
        Assert.DoesNotContain("ByRef<byte> searchSpace", result.OutputCode);
        // ... and its call site keeps the native ref as well.
        Assert.Matches(new Regex(@"Scan\(ref b\)"), result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        Assert.Equal((byte)7, (byte)InvokeRun(assembly));
    }

    [Fact]
    public void Unsafe_Ref_Parameter_With_RefReturn_Invocation_Keeps_Address_Semantics()
    {
        var result = Run(UnsafeRefReturnSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        Assert.Matches(new Regex(@"static int IndexOfLessThan\(ref byte searchSpace"),
            result.OutputCode);
        Assert.Matches(new Regex(@"ref System\.Runtime\.InteropServices\.MemoryMarshal\.GetReference\(span\)"),
            result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);
        // The scan must read the real span bytes, not a lifted stack copy.
        Assert.Equal(2, (int)InvokeRun(assembly));
    }

    [Fact]
    public void Non_Unsafe_Ref_Parameter_Is_Still_Rewritten_To_ByRef()
    {
        const string src = """
            public static class Api
            {
                public static int Read(ref int x) { return x + 1; }
            }

            public static class Use
            {
                public static int Run()
                {
                    int v = 41;
                    return Api.Read(ref v);
                }
            }
            """;
        var result = Run(src);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // The unsafe exemption must not leak into ordinary methods.
        Assert.Contains("ByRef<int> x", result.OutputCode);
        Assert.DoesNotContain("ref int x", result.OutputCode);

        var assembly = LoadAssembly(Verify(result.OutputCode).Compilation);
        Assert.Equal(42, (int)InvokeRun(assembly));
    }
}
