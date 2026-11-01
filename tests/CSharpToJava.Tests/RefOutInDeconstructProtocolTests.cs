using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS8129/CS1615 in the ref/out/in pass: tuple deconstruction
/// (<c>(X, Y, Z) = rhs</c>) binds the compiler-generated
/// <c>Deconstruct(out ...)</c> implicitly, so there is no invocation node the
/// statement lifter could rewrite. Re-signing Deconstruct to
/// <c>ByRef&lt;T&gt;</c> broke the language protocol; the pass must keep its
/// native out parameters.
/// </summary>
public class RefOutInDeconstructProtocolTests
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
                public static int ReadbackInt(ByRef<int> slot) => slot.Value;
                public static bool ReadbackBool(ByRef<bool> slot) => slot.Value;
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
            "roi-deconstruct-" + Guid.NewGuid().ToString("N"),
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
            "roi-deconstruct-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private const string DeconstructSource = """
        public struct TupleInt32Int32Int32
        {
            public int Item1;
            public int Item2;
            public int Item3;
            public TupleInt32Int32Int32(int a, int b, int c) { Item1 = a; Item2 = b; Item3 = c; }
            public void Deconstruct(out int item1, out int item2, out int item3)
            {
                item1 = Item1; item2 = Item2; item3 = Item3;
            }
        }

        public class Point
        {
            public int X { get; private set; }
            public int Y { get; private set; }
            public int Z { get; private set; }
            public Point(int x, int y, int z) => (X, Y, Z) = new TupleInt32Int32Int32(x, y, z);
            public void Other(int x, int y, int z) { (X, Y, Z) = new TupleInt32Int32Int32(x, y, z); }
        }
        """;

    [Fact]
    public void Tuple_Deconstruction_Keeps_Native_Out_On_Deconstruct()
    {
        var result = Run(DeconstructSource);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // Signature preserved: still native out params, not ByRef<T> holders.
        Assert.Contains("out int item1", result.OutputCode);
        Assert.Contains("out int item2", result.OutputCode);
        Assert.Contains("out int item3", result.OutputCode);
        Assert.DoesNotContain("ByRef<int> item1", result.OutputCode);
        // Deconstruction syntax itself survives.
        Assert.Contains("(X, Y, Z)", result.OutputCode);

        // Emit + execute: deconstruction still assigns the properties.
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-deconstruct-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var pointType = asm.GetType("Point")!;
        var ctor = pointType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) })!;
        var point = ctor.Invoke(new object[] { 1, 2, 3 });
        Assert.Equal(1, pointType.GetProperty("X")!.GetValue(point));
        Assert.Equal(2, pointType.GetProperty("Y")!.GetValue(point));
        Assert.Equal(3, pointType.GetProperty("Z")!.GetValue(point));

        pointType.GetMethod("Other", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(point, new object[] { 4, 5, 6 });
        Assert.Equal(4, pointType.GetProperty("X")!.GetValue(point));
        Assert.Equal(5, pointType.GetProperty("Y")!.GetValue(point));
        Assert.Equal(6, pointType.GetProperty("Z")!.GetValue(point));
    }

    [Fact]
    public void Ordinary_Out_Method_Is_Still_Rewritten_To_ByRef()
    {
        const string src = """
            public static class TryGetHelper
            {
                public static bool TryGet(out int v) { v = 42; return true; }
            }

            public static class Caller
            {
                public static int Run()
                {
                    TryGetHelper.TryGet(out var value);
                    return value;
                }
            }
            """;
        var result = Run(src);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // The exemption is scoped to Deconstruct only.
        Assert.Contains("ByRef<int> v", result.OutputCode);
        Assert.DoesNotContain("out int v", result.OutputCode);
    }
}
