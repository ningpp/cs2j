using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0206 in the ref/out/in pass: a <c>throw</c> statement whose
/// expression contains a liftable ref/out call was never statement-lifted
/// (<c>throw GetJsonReaderException(ref json, …)</c> in ThrowHelper.Serialization).
/// The main rewriter injected <c>json.Value</c> into the argument while the
/// <c>ref</c> keyword stayed, producing <c>ref json.Value</c> — CS0206.
/// </summary>
public class RefOutInThrowStatementTests
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
            "roi-throwstmt-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Assembly AssertOutputCompilesAndEmits(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-throwstmt-verify-" + Guid.NewGuid().ToString("N"),
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

    private const string RefStructThrowSource = """
        public ref struct MyReader
        {
            public int State;
            public MyReader(int state) { State = state; }
        }

        public static class Helper
        {
            public static System.Exception Make(ref MyReader r)
                => new System.InvalidOperationException(r.State.ToString());

            public static void ThrowIt(ref MyReader r)
            {
                throw Make(ref r);
            }
        }

        public static class Demo
        {
            public static string Run()
            {
                MyReader reader = new MyReader(7);
                try
                {
                    Helper.ThrowIt(ref reader);
                }
                catch (System.Exception e)
                {
                    return e.Message;
                }
                return "none";
            }
        }
        """;

    [Fact]
    public void Throw_With_Ref_Struct_Holder_Argument_Is_Lifted_Without_Ref_Value()
    {
        var result = Run(RefStructThrowSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotContain("ref r.Value", result.OutputCode);
        Assert.DoesNotContain("ref reader.Value", result.OutputCode);
        Assert.Contains("Make(r)", result.OutputCode);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        var run = asm.GetType("Demo")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal("7", run.Invoke(null, null));
    }

    private const string OutArgThrowSource = """
        public static class Helper
        {
            public static System.Exception Make(out int value)
            {
                value = 42;
                return new System.InvalidOperationException(value.ToString());
            }
        }

        public static class Demo
        {
            public static string Run()
            {
                int x = 0;
                try
                {
                    throw Helper.Make(out x);
                }
                catch (System.Exception e)
                {
                    return e.Message;
                }
            }
        }
        """;

    [Fact]
    public void Throw_With_Liftable_Out_Argument_Hoists_Call_Then_Throws_Temp()
    {
        var result = Run(OutArgThrowSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        var run = asm.GetType("Demo")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal("42", run.Invoke(null, null));
    }
}
