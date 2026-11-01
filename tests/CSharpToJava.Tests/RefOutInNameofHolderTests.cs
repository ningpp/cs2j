using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: <c>nameof(holder)</c> must keep its original identifier text.
/// A holder identifier (a rewritten <c>ref</c>/<c>out</c>/<c>in</c> parameter
/// or local) used as a bare <c>nameof</c> operand used to be renamed to its
/// shadow local (<c>__shadowN</c>) or have <c>.Value</c> appended, which
/// changes the baked-in string — e.g. the <c>ParamName</c> of an
/// <c>ArgumentOutOfRangeException(nameof(reader), …)</c> in
/// json/tests/JsonDocumentTests.cs.
/// </summary>
public class RefOutInNameofHolderTests
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
            "roi-nameof-" + Guid.NewGuid().ToString("N"),
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
            "roi-nameof-verify-" + Guid.NewGuid().ToString("N"),
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

    private const string NameofHolderSource = """
        public static class Api
        {
            public static void M(ref int x)
            {
                x++;
                throw new System.ArgumentOutOfRangeException(nameof(x), "expected x");
            }
        }

        public static class Use
        {
            public static string Run()
            {
                int v = 0;
                try
                {
                    Api.M(ref v);
                }
                catch (System.ArgumentOutOfRangeException e)
                {
                    return e.ParamName ?? "";
                }
                return "";
            }
        }
        """;

    [Fact]
    public void Nameof_Of_Shadowed_Holder_Keeps_Original_Parameter_Name()
    {
        var result = Run(NameofHolderSource);

        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("nameof(x)", result.OutputCode);
        Assert.DoesNotContain("nameof(__shadow", result.OutputCode);
        Assert.DoesNotContain("nameof(x.Value)", result.OutputCode);

        var asm = AssertOutputCompilesAndEmits(result.OutputCode);
        var run = asm.GetType("Use")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
        Assert.Equal("x", run.Invoke(null, null));
    }
}
