using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS9244/CS1503 in the ref/out/in pass: a declaration-form
/// <c>out</c> argument whose PARAMETER type is a ref struct (SDK json
/// <c>BuildSegmentedReader(out Utf8JsonReader reader, …)</c>) used to get a
/// <c>ByRef&lt;Utf8JsonReader&gt;</c> slot. <c>ByRef&lt;T&gt;</c> has no
/// <c>allows ref struct</c> constraint, so the slot itself was CS9244, and the
/// declaration pass had already converted the parameter to
/// <c>RefStructByRef&lt;Utf8JsonReader&gt;</c>, so the call was also CS1503.
/// The slot must therefore be a <c>RefStructByRef&lt;T&gt;</c> with a plain
/// <c>{target} = {slot}.Value;</c> read-back.
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
                public abstract T Value { get; set; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
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
    public void RefStruct_Out_Declaration_Arguments_Use_RefStructByRef_Slots()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // The slot holder must be RefStructByRef<MyReader>. A bare
        // `ByRef<MyReader>` type argument is CS9244 even when qualified
        // (negative look-behind keeps `RefStructByRef<MyReader>` out).
        Assert.Contains("RefStructByRef<MyReader>", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])ByRef<MyReader>"), result.OutputCode);

        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void RefStruct_Out_Discard_Does_Not_Emit_ByRef_Type_Argument()
    {
        var result = Run(Source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.DoesNotMatch(new Regex(@"(?<![A-Za-z])ByRef<MyReader>"), result.OutputCode);
        AssertOutputCompiles(result.OutputCode);
    }

    [Fact]
    public void Lifted_RefStruct_Out_Arguments_Emit_With_Declared_Local_Read_Back()
    {
        var result = Run(Source);
        var compilation = AssertOutputCompiles(result.OutputCode);

        // Emit as well: CS9244 on a ByRef<T> type argument surfaces at
        // declaration, but the slot must also be a valid IL local.
        var assembly = LoadAssembly(compilation);
        // `Fill(out _)` executes the discard lift path; the slot is a ref-struct
        // value that cannot cross the reflective boundary, so only this
        // parameterless entry point is invoked.
        Assert.Equal(1, InvokeInt(assembly, "RunDiscard"));

        // Both `out T x` and `out var x` write the slot value back into the
        // declared local (the slot is never the target of the read-back).
        Assert.Matches(new Regex(@"\br = __out\d+\.Value;"), result.OutputCode);
        Assert.Matches(new Regex(@"\br2 = __out\d+\.Value;"), result.OutputCode);
    }
}
