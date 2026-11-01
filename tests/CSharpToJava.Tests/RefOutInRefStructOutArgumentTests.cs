using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Round 17: a declaration-form <c>out</c> argument whose PARAMETER type is a
/// ref struct (SDK json <c>BuildSegmentedReader(out Utf8JsonReader reader, …)</c>)
/// keeps its NATIVE <c>out</c> form. <c>RefStructByRef&lt;T&gt;</c> cannot alias
/// a ref struct field (CS8170/CS9050), so no slot is allocated: the callee's
/// <c>out MyReader r</c> signature and every <c>out T x</c> / <c>out var x</c> /
/// <c>out _</c> call site stay unchanged.
/// </summary>
public class RefOutInRefStructOutArgumentTests
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
            public int State;
            public MyReader(int state) { State = state; }
        }

        public static class Api
        {
            public static void Fill(out MyReader r) { r = new MyReader(7); }

            public static int Run()
            {
                Fill(out MyReader r);
                Fill(out var r2);
                Fill(out _);
                return r.State + r2.State;
            }

            public static int RunDiscard()
            {
                Fill(out _);
                return 1;
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
            "roi-refstruct-out-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    private static Compilation Verify(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub, ParseOptions);
        return CSharpCompilation.Create(
            "roi-refstruct-out-verify-" + Guid.NewGuid().ToString("N"),
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
        var actual = assembly.GetType("Api")!
            .GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        return (int)actual!;
    }

    [Fact]
    public void RefStruct_Out_Declaration_Arguments_StayNative()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The declaration keeps `out MyReader r` and no holder type is emitted.
        Assert.Contains("public static void Fill(out MyReader r)", result.OutputCode);
        Assert.DoesNotContain("RefStructByRef", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])ByRef<MyReader>"), result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RefStruct_Out_Discard_StaysNative()
    {
        var result = Run(Source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("Fill(out _)", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])ByRef<MyReader>"), result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RefStruct_Out_Arguments_Emit_And_Run_With_Native_Locals()
    {
        var result = Run(Source);
        var compilation = AssertOutputCompiles(result.OutputCode);

        var assembly = LoadAssembly(compilation);
        Assert.Equal(1, InvokeInt(assembly, "RunDiscard"));

        // `out T x` and `out var x` declare the local directly: no slot and no
        // read-back statement is generated.
        Assert.Contains("Fill(out MyReader r)", result.OutputCode);
        Assert.Contains("Fill(out var r2)", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"\br = __out\d+\.Value;"), result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"\br2 = __out\d+\.Value;"), result.OutputCode);
    }
}
