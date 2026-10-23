using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS8115 in the ref/out/in pass: when a conditional (or
/// null-coalescing) expression containing a liftable ref/out call was
/// statement-spliced, a <c>throw</c> expression branch was emitted as the
/// RHS of an assignment (<c>__cond = throw ...;</c>), which is illegal —
/// a throw expression is not permitted there. The branch must become a
/// throw statement.
/// </summary>
public class RefOutInConditionalThrowTests
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
            "roi-throw-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Assembly EmitOutput(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-throw-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
    }

    private const string ConditionalSource = """
        static class Helper
        {
            public static int Compute(int seed, ref int count) { count++; return seed + count; }
        }

        public static class Demo
        {
            public static int Pick(bool flag)
            {
                int count = 0;
                return flag
                    ? Helper.Compute(10, ref count)
                    : throw new System.InvalidOperationException("boom");
            }
        }
        """;

    [Fact]
    public void Conditional_Throw_Branch_Becomes_Throw_Statement()
    {
        var result = Run(ConditionalSource);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotContain("= throw", result.OutputCode);

        var asm = EmitOutput(result.OutputCode);
        var pick = asm.GetType("Demo")!.GetMethod("Pick", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal(11, pick.Invoke(null, new object[] { true }));

        var ex = Assert.Throws<TargetInvocationException>(() => pick.Invoke(null, new object[] { false }));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("boom", ex.InnerException!.Message);
    }

    private const string CoalesceSource = """
        static class Helper
        {
            public static System.Exception MakeEx(ref int count)
            {
                count++;
                return new System.InvalidOperationException("boom" + count);
            }
        }

        public static class Demo
        {
            public static string Get(string? s)
            {
                int count = 0;
                return s ?? throw Helper.MakeEx(ref count);
            }
        }
        """;

    [Fact]
    public void Coalesce_Throw_Branch_Becomes_Throw_Statement()
    {
        var result = Run(CoalesceSource);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotContain("= throw", result.OutputCode);

        var asm = EmitOutput(result.OutputCode);
        var get = asm.GetType("Demo")!.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Equal("ok", get.Invoke(null, new object?[] { "ok" }));

        var ex = Assert.Throws<TargetInvocationException>(() => get.Invoke(null, new object?[] { null }));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("boom1", ex.InnerException!.Message);
    }
}
