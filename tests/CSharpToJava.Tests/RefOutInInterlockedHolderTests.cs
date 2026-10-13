using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0206 in the generated regular-expressions tree
/// (RegexCharClass cache sites): <c>Interlocked.Or(ref slot, …)</c> /
/// <c>Interlocked.CompareExchange(ref cache, …)</c> where the target is a
/// ref/out-in holder becomes <c>Interlocked.X(ref holder.Value, …)</c>, but
/// ByRef&lt;T&gt;.Value is a property — not a variable. The ref/out-in pass
/// maps these calls onto Cs2jInterlocked, whose lock-based implementation has
/// the same observable exchange/CAS semantics over any ByRef proxy kind.
/// </summary>
public class RefOutInInterlockedHolderTests
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
            public static class Cs2jInterlocked
            {
                public static T CompareExchange<T>(ByRef<T> slot, T value, T comparand)
                {
                    lock (slot)
                    {
                        var old = slot.Value;
                        if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(old, comparand))
                            slot.Value = value;
                        return old;
                    }
                }
                public static int Or(ByRef<int> slot, int value)
                {
                    lock (slot) { var old = slot.Value; slot.Value = old | value; return old; }
                }
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
        var tree = CSharpSyntaxTree.ParseText(src);
        var compilation = CSharpCompilation.Create(
            "roi-interlocked-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static void AssertOutputCompiles(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-interlocked-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private static string? Invoke(string source, bool withByRefStub)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-interlocked-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var demo = asm.GetType("Demo")!;
        return (string?)demo.GetMethod("Go", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    private const string Source = """
        using System.Threading;
        public static class Demo
        {
            private static string SetBits(ref int bits, int mask)
            {
                int original = Interlocked.Or(ref bits, mask);
                return original + "," + bits;
            }

            private static string InitCache(ref string cache)
            {
                if (cache == null)
                {
                    Interlocked.CompareExchange(ref cache, "ready", null);
                }
                return cache;
            }

            public static string Go()
            {
                int bits = 0x01;
                string r1 = SetBits(ref bits, 0x10);
                string cache = null;
                string r2 = InitCache(ref cache);
                // Second call must observe the non-null value and not overwrite it.
                string r3 = InitCache(ref cache);
                return r1 + "|" + r2 + "|" + r3;
            }
        }
        """;

    [Fact]
    public void Interlocked_Ref_Holder_Calls_Are_Mapped_To_Cs2jInterlocked()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // The illegal ref-to-property form is gone...
        Assert.DoesNotContain("Interlocked.Or(ref ", result.OutputCode);
        Assert.DoesNotContain("Interlocked.CompareExchange(ref ", result.OutputCode);
        Assert.DoesNotContain("ref bits.Value", result.OutputCode);
        Assert.DoesNotContain("ref cache.Value", result.OutputCode);
        // ...and mapped onto the support class with the holder as first argument.
        Assert.Contains("Cs2jInterlocked.Or(bits", result.OutputCode);
        Assert.Contains("Cs2jInterlocked.CompareExchange(cache", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(Source, false);
        var actual = Invoke(result.OutputCode, true);
        Assert.Equal(expected, actual);
        // Or returns the ORIGINAL value (1) and leaves bits = 0x11 (17).
        Assert.Equal("1,17|ready|ready", actual);
    }
}
