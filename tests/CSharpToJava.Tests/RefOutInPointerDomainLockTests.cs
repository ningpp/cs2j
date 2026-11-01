using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression (dotnet.text.json Serialization, order-dependent
/// <c>Expected: 64 Actual: 0</c> + stack overflow):
/// <c>JsonClassInfo.GetKey</c> declares
/// <c>ref byte reference = ref MemoryMarshal.GetReference(name)</c> and walks
/// that ADDRESS with <c>Unsafe.ReadUnaligned(ref reference)</c> /
/// <c>Unsafe.Add(ref reference, n)</c>. The pass is not allowed to proxy such a
/// ref local into a <c>ScopedByRef&lt;byte&gt;</c>: the statement lifter then
/// passes <c>ref &lt;stack copy&gt;</c> to <c>Unsafe.Add</c>, reading adjacent
/// stack memory, so the property-name key depended on the caller frame and
/// poisoned the shared <c>_propertyRefsSorted</c> cache (properties silently
/// missing, converters recursing until stack overflow).
/// <para>
/// A method whose body hands a ref local's address to a pointer/alias API
/// (<c>Unsafe.*</c> / <c>MemoryMarshal.*</c>) therefore keeps its native ref
/// local and pointer arithmetic; ordinary ref parameters are still lifted to
/// <c>ByRef&lt;T&gt;</c>.
/// </para>
/// </summary>
public class RefOutInPointerDomainLockTests
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

            public ref struct ScopedByRef<T>
            {
                private ref T _value;
                private ScopedByRef(ref T value) { _value = ref value; }
                public T Value
                {
                    get => _value;
                    set => _value = value;
                }
                public static ScopedByRef<T> Create(ref T value) => new ScopedByRef<T>(ref value);
            }
        }
        """;

    private const string PointerDomainSource = """
        using System;

        public static class Api
        {
            public static byte ReadAt(System.ReadOnlySpan<byte> span, int off)
            {
                ref byte reference = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(span);
                return System.Runtime.CompilerServices.Unsafe.Add(ref reference, off);
            }
        }

        public static class Use
        {
            public static byte Run()
            {
                var a = new byte[] { 1, 2, 3 };
                return Api.ReadAt(a.AsSpan(), 2);
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
            "roi-pointer-domain-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Compilation AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-pointer-domain-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors)
            + "\n===== OUTPUT =====\n" + code);
        return compilation;
    }

    [Fact]
    public void PointerDomain_Ref_Local_Keeps_Native_Ref_And_Unsafe_Addressing()
    {
        var result = Run(PointerDomainSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The ref local stays a native alias over the span's first element.
        Assert.True(result.OutputCode.Contains("ref byte reference = ref "), result.OutputCode);
        // No proxy was built for it, and no lifted stack copy reaches Unsafe.Add.
        Assert.DoesNotContain("ScopedByRef<byte>", result.OutputCode);
        Assert.DoesNotContain("ByRef<byte>", result.OutputCode);
        Assert.DoesNotContain("Unsafe.Add(ref __ref", result.OutputCode);

        var compilation = AssertOutputCompiles(result.OutputCode);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));

        Assert.Equal((byte)3, (byte)assembly.GetType("Use")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!);
    }

    [Fact]
    public void Ordinary_Ref_Parameter_Is_Still_Rewritten_To_ByRef()
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
        Assert.Contains("ByRef<int> x", result.OutputCode);
        Assert.DoesNotContain("ref int x", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }
}
