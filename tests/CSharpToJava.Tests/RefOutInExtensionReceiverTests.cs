using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS1929 in the ref/out/in pass: an extension method declared
/// with a converted <c>this ref</c> parameter
/// (<c>public static int GetPos(this ref MyReader reader)</c> becomes
/// <c>GetPos(this RefStructByRef&lt;MyReader&gt; reader)</c>) is invoked at call
/// sites in REDUCED form (<c>reader.GetPos()</c>) — the receiver is an implicit
/// argument 0, not part of the ArgumentList. Statement lifting ignored it and
/// the main rewriter injected <c>reader.Value</c> as the receiver, so the call
/// bound to the un-converted value type and produced
/// "no member GetPos … best extension needs RefStructByRef" (CS1929).
///
/// The fix lifts the reduced receiver like an explicit ref argument and
/// rewrites the call to its static form <c>Type.M(receiver, …)</c>, so the
/// callee receives the holder directly (holder receiver), or a
/// copy-in/copy-out slot (ref struct / value type receiver).
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
    public void ThisRef_Extension_Declaration_Is_Rewritten_To_Holder_Parameter()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // Declaration side: `this ref MyReader` becomes a ref-struct holder
        // parameter that keeps reference semantics (`this ref <holder>`).
        Assert.Contains("GetPos(this ref Cs2jByRef.RefStructByRef<MyReader>", result.OutputCode);
        Assert.Contains("ReadWithVerify(this ref Cs2jByRef.RefStructByRef<MyReader>", result.OutputCode);
    }

    [Fact]
    public void Reduced_Extension_Receiver_Is_Lifted_And_Call_Becomes_Static()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // No reduced receiver form survives: CS1929 would come straight back.
        Assert.DoesNotContain(".GetPos()", result.OutputCode);
        Assert.DoesNotContain(".ReadWithVerify()", result.OutputCode);
        Assert.DoesNotContain(".Bump()", result.OutputCode);

        // Rewritten to the fully qualified static call.
        Assert.Contains("global::JsonHelpersExt.GetPos(", result.OutputCode);
        Assert.Contains("global::JsonHelpersExt.ReadWithVerify(", result.OutputCode);
        Assert.Contains("global::JsonHelpersExt.Bump(", result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Holder_Receiver_Is_Never_Dereferenced_Through_Value_Property()
    {
        var result = Run(Source);
        AssertOutputCompiles(result.OutputCode);

        // Holder receiver (`ref MyReader reader` param) whose body mutates it:
        // the receiver is a RefStructByRef slot seeded from the holder's shadow
        // local and passed BY REFERENCE — never `reader.Value` (a ref struct
        // property return copy would swallow the callee's mutation).
        Assert.DoesNotContain("reader.Value.GetPos", result.OutputCode);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"global::JsonHelpersExt\.GetPos\(ref "), result.OutputCode);
        Assert.Matches(new System.Text.RegularExpressions.Regex(
            @"reader\.Value = __shadow\d+;"), result.OutputCode);

        // Ref struct local receiver: copy-in/copy-out slot.
        Assert.Contains("RefStructByRef<MyReader>", result.OutputCode);
        Assert.Contains("= __ref", result.OutputCode);
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
