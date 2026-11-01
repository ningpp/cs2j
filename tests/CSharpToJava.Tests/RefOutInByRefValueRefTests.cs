using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression: <c>ByRef&lt;T&gt;.Value</c> is a ref-returning property over the
/// holder's own storage (slot field / array element), not a get/set property.
/// A value-typed holder body therefore mutates the slot in place
/// (<c>b.Value.X++</c>) and no longer needs a shadow copy-in/copy-out local.
/// The previous get/set property forced a <c>__shadowN</c> local in every
/// method whose <c>ref</c> parameter was a struct, which inflated the
/// recursive call frames (Text.Json <c>DeepNestedJsonFileTest</c> stack
/// overflow) and dropped mutations on the exception path.
/// </summary>
public class RefOutInByRefValueRefTests
{
    private const string ByRefSupportStub = """
        namespace Cs2jByRef
        {
            public abstract class ByRef<T>
            {
                public abstract ref T Value { get; }
                public static ByRef<T> Create() => new SlotByRef<T>();
                public static ByRef<T> Create(T value) => new SlotByRef<T>(value);
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override ref T Value => ref _slot;
            }
        }
        """;

    private const string Source = """
        public struct Box { public int X; }

        public static class Api
        {
            public static void Mutate(ref Box b) { b.X++; }
        }

        public static class Demo
        {
            public static int Run()
            {
                Box b = default;
                Api.Mutate(ref b);
                return b.X;
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
            "roi-byref-value-ref-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    [Fact]
    public void ValueHolder_MutatesSlotThroughRefProperty_WithoutShadow()
    {
        var result = Run(Source);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);

        // Declaration side: the struct ref parameter is a ByRef<Box> holder.
        Assert.Contains("ByRef<Box> b", result.OutputCode);
        // Body side: member write lands on the ref-returned slot, not a copy.
        Assert.Contains("b.Value.X++", result.OutputCode);
        // No shadow copy-in/copy-out local survives for non-ref-like holders.
        Assert.DoesNotContain("__shadow", result.OutputCode);

        var assembly = Emit(result.OutputCode);
        var demo = assembly.GetType("Demo")!;
        var actual = demo.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        Assert.Equal(1, (int)actual!);
    }

    private static Assembly Emit(string output)
    {
        var tree = CSharpSyntaxTree.ParseText(output);
        var support = CSharpSyntaxTree.ParseText(ByRefSupportStub);
        var compilation = CSharpCompilation.Create(
            "roi-byref-value-ref-emit-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            + "\n===== OUTPUT =====\n" + output);
        return AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
    }
}
