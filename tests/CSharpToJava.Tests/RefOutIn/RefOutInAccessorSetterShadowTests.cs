using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests.RefOutIn;

/// <summary>
/// Regression: the setter lambda of an accessor proxy
/// (<c>ByRef&lt;T&gt;.Create(() =&gt; target, p =&gt; target = p)</c>) used a
/// fixed parameter name <c>v</c>. When the target expression referenced an
/// identifier also named <c>v</c> (a local, an index variable, a receiver …)
/// the lambda parameter shadowed it and the write-back silently collapsed into
/// a self-assignment (<c>v =&gt; v = v</c>). GraphLayout's
/// CubicBezierSegment.Trim/LengthPartial hit exactly that shape.
/// </summary>
public class RefOutInAccessorSetterShadowTests
{
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
                // out 值类型读回（与 src/Cs2jRefOutIn/ByRef.cs 的 ReadbackX 一致，Java 半区 null→default 桥）
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static int ReadbackInt(ByRef<uint> slot) => (int)slot.Value;
                public static int ReadbackInt(ByRef<byte> slot) => slot.Value;
                public static short ReadbackShort(ByRef<short> slot) => slot.Value;
                public static short ReadbackShort(ByRef<ushort> slot) => (short)slot.Value;
                public static byte ReadbackByte(ByRef<sbyte> slot) => (byte)slot.Value;
                public static long ReadbackLong(ByRef<long> slot) => slot.Value;
                public static long ReadbackLong(ByRef<ulong> slot) => (long)slot.Value;
                public static char ReadbackChar(ByRef<char> slot) => slot.Value;
                public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
                public static float ReadbackFloat(ByRef<float> slot) => slot.Value;
                public static double ReadbackDouble(ByRef<double> slot) => slot.Value;
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

    private static IEnumerable<MetadataReference> PlatformReferences()
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

    private static byte[] CompilePeer(string peerSrc)
    {
        var compilation = CSharpCompilation.Create(
            "roi-shadow-peer-" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(peerSrc) },
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success,
            "peer emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return stream.ToArray();
    }

    private static (string Output, Assembly Assembly) RewriteCompileLoad(
        string src, string? peerSrc = null)
    {
        var peerImage = peerSrc == null ? null : CompilePeer(peerSrc);
        var refs = PlatformReferences().ToList();
        if (peerImage != null)
            refs.Add(MetadataReference.CreateFromImage(peerImage));

        var tree = CSharpSyntaxTree.ParseText(src);
        var modelCompilation = CSharpCompilation.Create(
            "roi-shadow-model-" + Guid.NewGuid().ToString("N"),
            new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var result = new RefOutInRewriter().Rewrite(src, modelCompilation.GetSemanticModel(tree));
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        var outTree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var supportTree = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-shadow-emit-" + Guid.NewGuid().ToString("N"),
            new[] { outTree, supportTree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            + "\n===== OUTPUT =====\n" + result.OutputCode);

        if (peerImage != null)
            AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(peerImage));
        var assembly = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(stream.ToArray()));
        return (result.OutputCode, assembly);
    }

    private static object? InvokeRun(Assembly assembly) =>
        assembly.GetType("C", throwOnError: true)!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);

    [Fact]
    public void RefArgument_LocalNamedLikeSetterParameter_StillWritesBack()
    {
        const string src = """
            public class C
            {
                public static void M(ref double v) { v = 1.0; }
                public static double Run()
                {
                    double v = 1.3;
                    M(ref v);
                    return v;
                }
            }
            """;

        var (output, assembly) = RewriteCompileLoad(src);

        // 修复前：setter 形参 v 遮蔽了同名局部 → `v => v = v` 自赋值，写回丢失。
        Assert.DoesNotContain("v => v = v", output);
        Assert.Equal(1.0, (double)InvokeRun(assembly)!);
    }

    [Fact]
    public void RefLocal_ElementAccessIndexNamedLikeSetterParameter_WritesBackThroughIndex()
    {
        const string peerSrc = """
            public sealed class IntBox
            {
                private readonly int[] _data = new int[16];
                public ref int this[int i] { get { return ref _data[i]; } }
            }
            """;
        const string src = """
            public class C
            {
                public static int Run()
                {
                    var box = new IntBox();
                    int v = 2;
                    ref int x = ref box[v];
                    x = 10;
                    return box[2];
                }
            }
            """;

        var (output, assembly) = RewriteCompileLoad(src, peerSrc);

        // 修复前：setter 形参 v 遮蔽索引变量 → `v => box[v] = v` 写到 box[10]。
        Assert.DoesNotContain("v => box[v] = v", output);
        Assert.Equal(10, (int)InvokeRun(assembly)!);
    }
}
