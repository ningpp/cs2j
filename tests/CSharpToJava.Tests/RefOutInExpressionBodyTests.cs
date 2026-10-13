using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS0127 in the ref/out/in pass: a VOID expression-bodied
/// method whose arrow body contains a liftable out/ref call
/// (<c>public new void M(Uri u, out E e) => base.M(u, out e);</c> in
/// UriParserTest) was converted to a block body that unconditionally emitted
/// <c>return &lt;lifted call&gt;;</c> — illegal against a void signature.
/// </summary>
public class RefOutInExpressionBodyTests
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
            "roi-arrow-" + Guid.NewGuid().ToString("N"),
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
            "roi-arrow-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private const string VoidArrowSource = """
        public class Base
        {
            public void Initialize(object uri, out System.Exception parsingError)
            {
                parsingError = new System.InvalidOperationException(uri.ToString());
            }
        }

        public sealed class Derived : Base
        {
            public new void Initialize(object uri, out System.Exception parsingError)
                => base.Initialize(uri, out parsingError);
        }

        public static class Demo
        {
            public static string Run()
            {
                new Derived().Initialize("u", out var e);
                return e.Message;
            }
        }
        """;

    [Fact]
    public void Void_Expression_Body_With_Liftable_Out_Call_Becomes_Expression_Statement()
    {
        var result = Run(VoidArrowSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        AssertOutputCompiles(result.OutputCode);

        // Executed behavior: the out value still flows through base.
        var tree = CSharpSyntaxTree.ParseText(result.OutputCode);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-arrow-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var actual = (string?)asm.GetType("Demo")!
            .GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
        Assert.Equal("u", actual);
    }

    [Fact]
    public void NonVoid_Expression_Body_With_Liftable_Ref_Call_Still_Returns_Value()
    {
        const string src = """
            static class Helper
            {
                public static int Compute(int seed, ref int count) { count++; return seed + count; }
            }
            class C
            {
                private int Bump(int seed, ref int count) => Helper.Compute(seed, ref count) * 2;

                public int M()
                {
                    int count = 0;
                    return Bump(40, ref count);
                }
            }
            """;
        var result = Run(src);
        Assert.True(result.Changed);
        AssertOutputCompiles(result.OutputCode);
        Assert.Contains("return", result.OutputCode);
    }
}
