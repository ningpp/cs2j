using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Repro for SDK json <c>JsonClassInfo.Cache.cs</c>:
/// <c>ref byte reference = ref MemoryMarshal.GetReference(name);</c>.
/// <c>MemoryMarshal.GetReference</c> is a framework (metadata) method whose
/// declaration the pass never rewrites, so it still returns a native
/// <c>ref T</c>. The old invocation branch assumed every ref-returning call
/// was an internal method already rewritten to <c>ByRef&lt;T&gt;</c>, producing
/// <c>ByRef&lt;byte&gt; reference = MemoryMarshal.GetReference(name);</c>
/// → CS0029.
/// </summary>
public class RefOutInExternalRefReturnLocalTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }

            // ref struct 接收器的元素别名（与 src/Cs2jRefOutIn/ByRef.cs 一致）
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

            public ref struct ReadOnlyScopedByRef<T>
            {
                private readonly ref T _value;
                private ReadOnlyScopedByRef(ref readonly T value)
                {
                    _value = ref System.Runtime.CompilerServices.Unsafe.AsRef(in value);
                }
                public T Value => _value;
                public static ReadOnlyScopedByRef<T> Create(in T value) =>
                    new ReadOnlyScopedByRef<T>(in value);
            }
        }
        """;

    private static readonly CSharpParseOptions ParseOptions =
        new CSharpParseOptions(LanguageVersion.Latest);

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
            "roi-ext-ref-return-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-ext-ref-return-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors) +
            "\n--- output ---\n" + code);
    }

    private const string RealSource = """
        public static class NameKey
        {
            public static ulong GetKey(System.ReadOnlySpan<byte> name)
            {
                ref byte reference = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(name);
                int length = name.Length;
                ulong key = (ulong)reference;
                return key + (ulong)length;
            }
        }
        """;

    [Fact]
    public void External_RefReturn_Local_Uses_ScopedByRef_Alias()
    {
        var result = Run(RealSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        Assert.Contains("ScopedByRef<byte> reference = ScopedByRef<byte>.Create(ref ",
            result.OutputCode);
        Assert.DoesNotContain("ByRef<byte> reference = System.Runtime.InteropServices.MemoryMarshal"
            + ".GetReference", result.OutputCode);
    }

    private const string ReadOnlySource = """
        public static class Peek
        {
            public static byte First(System.ReadOnlySpan<byte> name)
            {
                ref readonly byte reference = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(name);
                return reference;
            }
        }
        """;

    [Fact]
    public void External_RefReadOnlyReturn_Local_Uses_ReadOnlyScopedByRef_Alias()
    {
        var result = Run(ReadOnlySource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);
        Assert.Contains("ReadOnlyScopedByRef<byte> reference = ReadOnlyScopedByRef<byte>.Create(in ",
            result.OutputCode);
    }

    [Fact]
    public void External_RefReturn_Local_Shares_Storage_Both_Ways()
    {
        const string src = """
            public static class Alias
            {
                public static ulong Bump(byte[] data)
                {
                    ref byte reference = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(new System.Span<byte>(data));
                    ulong key = (ulong)reference;
                    reference = (byte)(key + 1);
                    return key + (ulong)data[0];
                }
            }
            """;

        var result = Run(src);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        var tree = CSharpSyntaxTree.ParseText(result.OutputCode, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        var compilation = CSharpCompilation.Create(
            "roi-ext-ref-return-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));

        // data = { 1, 2 }: read through alias → 1, write through alias → data[0] = 2.
        var actual = (ulong)asm.GetType("Alias")!
            .GetMethod("Bump", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { new byte[] { 1, 2 } })!;
        Assert.Equal(3UL, actual);
    }
}
