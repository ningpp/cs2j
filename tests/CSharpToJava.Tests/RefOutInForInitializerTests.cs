using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0103/CS0165 after the for→while(true) reconstruction:
/// when the for header's initializer/incrementor contains a liftable call
/// (NOT a mere holder read) the rewriter rebuilds the loop as
///
///   for (I; C; U) B → I; while (true) { C-splice; if (!C') break; B; U; }
///
/// but the custom path only emitted the statements hoisted out from INSIDE
/// the initializers — the initializer declaration/assignment I itself was
/// silently dropped. A holder-slot read in the condition is not a lift and
/// keeps the for form (loop-variable scope is preserved, CS0136).
///
/// Real sites: System.UriHelper (missing `int i = 0;`) and
/// System.IPv6AddressHelper (missing `i = start;`).
/// </summary>
public class RefOutInForInitializerTests
{
    // Mirrors src/Cs2jRefOutIn/ByRef.cs: Value is an abstract property so the
    // accessor factory writes THROUGH to the captured ref target.
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
            "roi-forinit-" + Guid.NewGuid().ToString("N"),
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
            "roi-forinit-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    /// <summary>Compiles source to an in-memory assembly and invokes Demo.Go().</summary>
    private static string? Invoke(string source, bool withByRefStub)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-forinit-emit-" + Guid.NewGuid().ToString("N"),
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

    private static string? RunRewriteAndEmit(string source, out RefOutInRewriterResult result)
    {
        var expected = Invoke(source, false);
        result = Run(source);
        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);
        var actual = Invoke(result.OutputCode, true);
        Assert.Equal(expected, actual);
        return actual;
    }

    // (a) holder in the condition forces the reconstruction path; the
    // declared loop variable with its initializer must be emitted once
    // before the while(true) (UriHelper.cs shape: CS0103 before the fix).
    private const string DeclaredCounterSource = """
        public class Demo
        {
            static int CountTo(ref int limit)
            {
                int sum = 0;
                for (int i = 0; i < limit; ++i)
                    sum += i;
                return sum;
            }

            public static string Go()
            {
                int limit = 5;
                int r = CountTo(ref limit);
                return r + "," + limit;
            }
        }
        """;

    [Fact]
    public void For_With_Declared_Counter_And_Holder_Condition_Keeps_Initializer_Before_While()
    {
        var actual = RunRewriteAndEmit(DeclaredCounterSource, out var result);

        // A holder read in the condition is injected in place; the loop keeps
        // its for form (its declared counter must still be emitted).
        Assert.Contains("for (int i = 0;", result.OutputCode);
        Assert.DoesNotContain("while (true)", result.OutputCode);
        Assert.Equal("10,5", actual);
    }

    // (b) externally declared loop variable: the assignment initializer must
    // stay in the for header (kept as-is for holder reads), otherwise the
    // first condition read hits CS0165 (IPv6AddressHelper.cs shape).
    private const string OuterDeclaredSource = """
        public class Demo
        {
            static int FromStart(int start, ref int limit)
            {
                int sum = 0;
                int i;
                for (i = start; i < limit; ++i)
                    sum += i;
                return sum * 100 + i;
            }

            public static string Go()
            {
                int limit = 5;
                return FromStart(2, ref limit) + "";
            }
        }
        """;

    [Fact]
    public void For_With_Outer_Declared_Variable_Keeps_Initializer_Assignment_Before_While()
    {
        var actual = RunRewriteAndEmit(OuterDeclaredSource, out var result);

        Assert.Contains("i = start;", result.OutputCode);
        Assert.DoesNotContain("while (true)", result.OutputCode);
        Assert.Equal("905", actual);
    }

    // (c) initializer containing a liftable out call: the slot/temp hoist
    // statements must lead the initializer declaration, whose initializer
    // then reads the call result temp.
    private const string HoistedInitializerSource = """
        public class Demo
        {
            static int Init(out int x)
            {
                x = 40;
                return 2;
            }

            static int Run(ref int limit)
            {
                int x;
                int sum = 0;
                for (int i = Init(out x); i < limit; ++i)
                    sum += i;
                return sum + x;
            }

            public static string Go()
            {
                int limit = 5;
                return Run(ref limit) + "";
            }
        }
        """;

    [Fact]
    public void For_Initializer_With_Liftable_Out_Call_Hoists_Before_The_Declaration()
    {
        var actual = RunRewriteAndEmit(HoistedInitializerSource, out var result);

        int hoistIndex = result.OutputCode.IndexOf("__call", StringComparison.Ordinal);
        int declIndex = result.OutputCode.IndexOf("int i = ", StringComparison.Ordinal);
        int whileIndex = result.OutputCode.IndexOf("while (true)", StringComparison.Ordinal);
        Assert.True(hoistIndex >= 0, "expected a hoisted call-temp statement:\n" + result.OutputCode);
        Assert.True(declIndex >= 0, "missing `int i = …` declaration:\n" + result.OutputCode);
        Assert.True(hoistIndex < declIndex, "hoisted call must precede the initializer declaration");
        Assert.True(declIndex < whileIndex);
        Assert.Equal("49", actual);
    }

    // (d) multiple declarators in one for header: every initializer must
    // survive in the single merged for declaration (holder reads are injected
    // in place and keep the for form).
    private const string MultipleDeclaratorsSource = """
        public class Demo
        {
            static int Multi(ref int limit)
            {
                int sum = 0;
                for (int i = 0, j = 1; i < limit; ++i)
                    sum += i + j;
                return sum;
            }

            public static string Go()
            {
                int limit = 5;
                return Multi(ref limit) + "";
            }
        }
        """;

    [Fact]
    public void For_With_Multiple_Declarators_Keeps_Every_Initializer()
    {
        var actual = RunRewriteAndEmit(MultipleDeclaratorsSource, out var result);

        Assert.Contains("int i = 0, j = 1;", result.OutputCode);
        Assert.DoesNotContain("while (true)", result.OutputCode);
        Assert.Equal("15", actual);
    }
}
