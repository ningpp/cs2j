using System.Reflection;
using System.Runtime.Loader;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS1677 in the generated regular-expressions tree
/// (Regex.Cache.MatchCallback sites): a held delegate lambda — one whose
/// delegate's Invoke parameter is <c>ref</c> — passed as a by-value argument
/// of an INVOCATION THAT IS STATEMENT-LIFTED (another argument is ref/out) was
/// never seen by the ref/out/in rewriter. The lifter pruned lambdas from its
/// structural walk and the enclosing statement is rebuilt from fragments that
/// the main traversal never visits, so the lambda kept its <c>ref</c>
/// parameter while the callee had been rewritten to expect
/// <c>ByRef&lt;T&gt;</c>. The fix converts such lambdas (held or not — a
/// non-held lambda may still capture an outer holder) through the normal
/// owner visit while the node is still bound to the original semantic model.
/// </summary>
public class RefOutInHeldDelegateLambdaTests
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
            "roi-heldlam-" + Guid.NewGuid().ToString("N"),
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
            "roi-heldlam-verify-" + Guid.NewGuid().ToString("N"),
            new[] { tree, support },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "ref/out/in output failed to compile:\n" + string.Join("\n", errors));
    }

    private static string? Invoke(string source, string typeName, string method, bool withByRefStub)
    {
        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-heldlam-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var type = asm.GetType(typeName)!;
        return (string?)type.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null);
    }

    // State is a reference type on purpose: in the real pipeline the tuple
    // pass turns the callback state into a generated tuple CLASS, so member
    // mutation through the abstract ByRef<T>.Value property is legal (a struct
    // would hit CS1612, the separate property-ref scenario covered elsewhere).
    private const string HeldLambdaArgumentSource = """
        namespace DemoNs;
        public class State { public int A; public int Count; }
        internal delegate bool Callback(ref State state, int match);
        internal class Runner
        {
            public int Run(ref State state, Callback cb)
            {
                int i = 0;
                while (cb(ref state, i)) i++;
                return i;
            }
        }
        public static class Demo
        {
            public static string Go()
            {
                var runner = new Runner();
                var state = new State();
                runner.Run(ref state, (ref State s, int m) =>
                {
                    s.A += m;
                    return ++s.Count < 3;
                });
                return state.A + "," + state.Count;
            }
        }
        """;

    [Fact]
    public void Held_Ref_Delegate_Lambda_Argument_Of_Lifted_Call_Is_Converted()
    {
        var result = Run(HeldLambdaArgumentSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // The held lambda parameter becomes ByRef<T> and body accesses go through .Value.
        Assert.Contains("(ByRef<State> s, int m)", result.OutputCode);
        Assert.Contains("s.Value.A += m", result.OutputCode);
        Assert.Contains("++s.Value.Count", result.OutputCode);
        Assert.DoesNotContain("(ref State s", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(HeldLambdaArgumentSource, "DemoNs.Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "DemoNs.Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("3,3", actual);
    }

    // A non-held lambda (no ref delegate parameter) passed to a lifted call
    // must simply pass THROUGH the splicer's owner visit: the call is still
    // lifted around it and the lambda body stays untouched. (An earlier
    // version of this fix re-entered the lifter and discarded the outer
    // call's slot declarations.)
    private const string PlainLambdaArgumentSource = """
        public class Demo
        {
            static int Consume(int seed, System.Func<int> f, out int z)
            {
                z = seed + f();
                return z;
            }

            static int Inner()
            {
                return Consume(7, () => 21, out int z);
            }

            public static string Go()
            {
                return Inner() + "";
            }
        }
        """;

    [Fact]
    public void NonHeld_Lambda_Argument_Passes_Through_And_Call_Is_Still_Lifted()
    {
        var result = Run(PlainLambdaArgumentSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        // The out argument is proxied (no `out` survives at the call site)...
        Assert.DoesNotContain("out int z", result.OutputCode.Replace("static int Consume", ""));
        Assert.Contains("ByRef<int>", result.OutputCode);
        // ...while the plain lambda is retained verbatim.
        Assert.Contains("() => 21", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(PlainLambdaArgumentSource, "Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("28", actual);
    }

    private const string NestedHeldLambdaSource = """
        public class State { public int A; }
        internal delegate bool Callback(ref State state, int match);
        internal static class Hub
        {
            public static int RunCb(Callback cb)
            {
                var st = new State();
                return cb(ref st, 1) ? 1 : 0;
            }

            public static int Pick(int x, System.Func<int, int> wrap, out int z)
            {
                z = wrap(x);
                return z;
            }
        }
        public static class Demo
        {
            public static string Go()
            {
                // Pick is lifted (out z); its lambda argument is NOT held, but
                // contains a nested held delegate lambda that must be converted
                // through the ordinary descent once the outer lambda is visited.
                Hub.Pick(3, k => Hub.RunCb((ref State s, int m) =>
                {
                    s.A += m + k;
                    return false;
                }), out int z);
                return z + "";
            }
        }
        """;

    [Fact]
    public void Held_Lambda_Nested_Inside_NonHeld_Lambda_Argument_Is_Converted()
    {
        var result = Run(NestedHeldLambdaSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("ByRef<State> s", result.OutputCode);
        Assert.Contains("s.Value.A += m + k", result.OutputCode);
        AssertOutputCompiles(result.OutputCode);

        var expected = Invoke(NestedHeldLambdaSource, "Demo", "Go", false);
        var actual = Invoke(result.OutputCode, "Demo", "Go", true);
        Assert.Equal(expected, actual);
        Assert.Equal("0", actual);
    }
}
