using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS1929 in the ref/out/in pass, and the round-17 native
/// ref-like rule: a <c>this ref</c> extension parameter of a VALUE type
/// (<c>GetPos(this ref Counter counter)</c>) is converted to a holder and its
/// reduced call site (<c>counter.GetPos()</c>) is lifted to the static form
/// <c>Type.M(receiver, …)</c>. A ref-like receiver (<c>this ref MyReader</c>)
/// keeps its NATIVE form: <c>RefStructByRef&lt;T&gt;</c> cannot alias a ref
/// struct (CS8170/CS9050), so the reduced call stays as written.
/// </summary>
public class RefOutInExtensionReceiverTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
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

    private const string Source = """
        public ref struct MyReader
        {
            private int _pos;
            public MyReader(int pos) { _pos = pos; }
            public int Position => _pos;
            public bool Read() { _pos++; return true; }
        }

        public struct Counter
        {
            public int Hits;
        }

        internal static class JsonHelpersExt
        {
            public static int GetPos(this ref MyReader reader) => reader.Position;

            public static void ReadWithVerify(this ref MyReader reader)
            {
                bool result = reader.Read();
                System.Diagnostics.Debug.Assert(result);
            }

            public static int Bump(this ref Counter counter)
            {
                counter.Hits++;
                return counter.Hits;
            }
        }

        public static class Demo
        {
            public static int ViaHolder(ref MyReader reader)
            {
                int pos = reader.GetPos();
                reader.ReadWithVerify();
                return pos;
            }

            public static int ViaLocal()
            {
                MyReader r = new MyReader(7);
                r.ReadWithVerify();
                return r.GetPos();
            }

            public static int ViaValueTypeLocal()
            {
                Counter c = default;
                return c.Bump();
            }

            public static int Run()
            {
                MyReader reader = new MyReader(5);
                return ViaHolder(ref reader);
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
            "roi-extreceiver-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Compilation Verify(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        return CSharpCompilation.Create(
            "roi-extreceiver-verify-" + Guid.NewGuid().ToString("N"),
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
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
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

    private static int InvokeInt(Assembly assembly, string methodName)
    {
        var actual = assembly.GetType("Demo")!
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        return (int)actual!;
    }

    [Fact]
    public void RefLikeThisRef_Extension_Declaration_Stays_Native()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // Ref-like receiver: `this ref MyReader` keeps its native form.
        Assert.Contains("GetPos(this ref MyReader", result.OutputCode);
        Assert.Contains("ReadWithVerify(this ref MyReader", result.OutputCode);
        Assert.DoesNotContain("RefStructByRef<MyReader>", result.OutputCode);
        // Value-type receiver: still converted to a ByRef holder (its Value is
        // a ref property, so the holder itself is passed by value).
        Assert.Contains("Bump(this ByRef<Counter>", result.OutputCode);
    }

    [Fact]
    public void RefLike_Receiver_Stays_Reduced_ValueType_Receiver_Is_Lifted()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // A ref-like receiver keeps the native reduced extension call.
        Assert.Contains(".GetPos()", result.OutputCode);
        Assert.Contains(".ReadWithVerify()", result.OutputCode);

        // A value-type receiver is lifted to the fully qualified static call.
        Assert.DoesNotContain(".Bump()", result.OutputCode);
        Assert.Contains("global::JsonHelpersExt.Bump(", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RefLike_Receiver_Is_Never_Dereferenced_Through_Value_Property()
    {
        var result = Run(Source);
        AssertOutputCompiles(result.OutputCode);

        // Ref-like receiver (`ref MyReader reader` param) is native: never
        // dereferenced through a holder value property and never shadowed.
        Assert.DoesNotContain("reader.Value.GetPos", result.OutputCode);
        Assert.DoesNotContain("__shadow", result.OutputCode);
        Assert.DoesNotContain("RefStructByRef<MyReader>", result.OutputCode);

        // Value-type local receiver: copy-in/copy-out ByRef<Counter> slot.
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"ByRef<Counter> __ref\d+ = ByRef<Counter>\.Create\(c\)"),
            result.OutputCode);
    }

    [Fact]
    public void Rewritten_Call_Sites_Still_Flow_Values_At_Runtime()
    {
        var result = Run(Source);
        var compilation = AssertOutputCompiles(result.OutputCode);
        var assembly = LoadAssembly(compilation);

        // __ref slots are `ref struct` values and cannot cross a reflective
        // call boundary, so only the parameterless entry points run.
        // 8 = 7 (copy-in of the receiver into the slot reached the MyReader
        // ctor value) + 1 (ReadWithVerify's `this ref` mutation now flows back
        // out of the holder: 7 would mean the copy-out was swallowed).
        Assert.Equal(8, InvokeInt(assembly, "ViaLocal"));
        // Value-type receiver through ByRef<Counter>: the mutation reaches the
        // local through the copy-out read-back.
        Assert.Equal(1, InvokeInt(assembly, "ViaValueTypeLocal"));
        Assert.Equal(5, InvokeInt(assembly, "Run"));
    }
}
