using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for CS1615 at internal ref/out call sites (the UriHelper
/// UnescapeString family in the csharp-sdk convert). The pass binds ONE
/// semantic model over the WHOLE solution graph, so a per-project source
/// substitution (the test fake dotnet.system.Uri coexisting with production
/// Uri) makes an UNRELATED argument ambiguous (CS0229). Roslyn then returns
/// Symbol=null with every overload reported as
/// [OverloadResolutionFailure]; the old rewriter treated the call as
/// non-internal/non-liftable and left the bare `ref count` in place while the
/// declaration side had already been rewritten to ByRef — CS1615 in each
/// project's own (unambiguous) build.
///
/// The fallback lifts when ALL candidates are internal source methods and they
/// agree on every syntactically-byref parameter's RefKind+type (plus arity and
/// void-ness). Ambiguity is reproduced here with a type that converts
/// implicitly to BOTH overloads' first parameter types; the byref shape is
/// identical, so the call must still be lifted. The rewritten output compiles
/// in the "per-project view" (one conversion dropped), which is exactly what
/// msbuild sees.
/// </summary>
public class RefOutInAmbiguousOverloadTests
{
    // Mirrors src/Cs2jRefOutIn/ByRef.cs.
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
            "roi-amb-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(tree);
        return new RefOutInRewriter().Rewrite(src, model);
    }

    /// <summary>
    /// Emits and invokes static <c>C.M()</c> returning int. Lines carrying the
    /// sentinel are dropped first — used to simulate the per-project compilation
    /// where the fake substitution (and hence the ambiguity) does not exist.
    /// </summary>
    private static int InvokeInt(string source, bool withByRefStub, string? dropSentinel = null)
    {
        if (dropSentinel != null)
        {
            source = string.Join("\n",
                source.Split('\n').Where(l => !l.Contains(dropSentinel)));
        }

        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(source) };
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-amb-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var c = asm.GetType("C")!;
        return (int)c.GetMethod("M", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    // Amb converts to BOTH int and string (no implicit conversion between
    // them) so `Consume(a, ref count)` fails
    // overload resolution (CS0121) while the ref shape is identical across the
    // two internal candidates. The string conversion line carries the
    // sentinel: dropping it reproduces the per-project build in which only the
    // int overload is applicable.
    private const string RefShapeSource = """
        class Amb
        {
            public static implicit operator int(Amb a) => 10;
            public static implicit operator string(Amb a) => "zz"; //__CS2J_DROP__
        }
        static class Helper
        {
            public static void Consume(int x, ref int count) => count += x;
            public static void Consume(string x, ref int count) => count += x.Length;
        }
        class C
        {
            public static int M()
            {
                Amb a = new Amb();
                int count = 0;
                Helper.Consume(a, ref count);
                return count;
            }
        }
        """;

    [Fact]
    public void Ambiguous_Overload_With_Agreeing_Ref_Shape_Is_Still_Lifted()
    {
        // Sanity: the merged source really is an overload-resolution failure.
        var preTree = CSharpSyntaxTree.ParseText(RefShapeSource);
        var preComp = CSharpCompilation.Create(
            "roi-amb-pre-" + Guid.NewGuid().ToString("N"),
            new[] { preTree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var preInv = preTree.GetRoot().DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Single();
        var preInfo = preComp.GetSemanticModel(preTree).GetSymbolInfo(preInv);
        Assert.Null(preInfo.Symbol);
        Assert.Equal(CandidateReason.OverloadResolutionFailure, preInfo.CandidateReason);
        Assert.Equal(2, preInfo.CandidateSymbols.Length);

        var result = Run(RefShapeSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        // The ref slot is materialized and the bare `ref count` argument is gone.
        Assert.Contains("ByRef", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"Consume\([^;]*\bref\s+count\b"),
            result.OutputCode);

        // Per-project view: unique resolution against the int overload, and the
        // lifted call returns the same value the original code would.
        Assert.Equal(10, InvokeInt(RefShapeSource, false, "__CS2J_DROP__"));
        Assert.Equal(10, InvokeInt(result.OutputCode, true, "__CS2J_DROP__"));
    }

    private const string OutShapeSource = """
        class Amb
        {
            public static implicit operator int(Amb a) => 10;
            public static implicit operator string(Amb a) => "zz"; //__CS2J_DROP__
        }
        static class Helper
        {
            public static void Produce(int x, out int r) { r = x + 1; }
            public static void Produce(string x, out int r) { r = x.Length + 2; }
        }
        class C
        {
            public static int M()
            {
                Amb a = new Amb();
                Helper.Produce(a, out int got);
                return got;
            }
        }
        """;

    [Fact]
    public void Ambiguous_Overload_With_Agreeing_Out_Shape_Is_Still_Lifted()
    {
        var result = Run(OutShapeSource);

        Assert.True(result.Changed);
        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("ByRef", result.OutputCode);
        Assert.DoesNotMatch(new Regex(@"Produce\([^;]*\bout\b"),
            result.OutputCode);

        Assert.Equal(11, InvokeInt(OutShapeSource, false, "__CS2J_DROP__"));
        Assert.Equal(11, InvokeInt(result.OutputCode, true, "__CS2J_DROP__"));
    }

    // The fallback maps arguments POSITIONALLY, so a named value argument is
    // rejected even when the byref shape agrees (the rewriter does not
    // re-implement named-argument binding).
    [Fact]
    public void Ambiguous_Overload_With_Named_Value_Argument_Is_Not_Lifted()
    {
        const string src = """
            class Amb
            {
                public static implicit operator int(Amb a) => 10;
                public static implicit operator string(Amb a) => "zz";
            }
            static class Helper
            {
                public static void Consume(int x, ref int count) => count += x;
                public static void Consume(string x, ref int count) => count += x.Length;
            }
            class C
            {
                public static void M()
                {
                    Amb a = new Amb();
                    int count = 0;
                    Helper.Consume(x: a, ref count);
                    System.Console.WriteLine(count);
                }
            }
            """;

        var result = Run(src);

        // Declarations are always lifted to ByRef; the CALL must stay bare
        // because positional mapping cannot handle the named argument.
        var callLine = result.OutputCode.Split('\n')
            .First(l => l.Contains("Consume(x:"));
        Assert.Contains("ref count", callLine);
        Assert.Equal(2, new Regex(@"ByRef<int>").Matches(result.OutputCode).Count);
    }

    // Two applicable candidates whose params tails differ (both bind in
    // EXPANDED form with zero extras): the positional mapping cannot cover a
    // params array, so lifting is refused.
    [Fact]
    public void Ambiguous_Overload_With_Params_Candidates_Is_Not_Lifted()
    {
        const string src = """
            class Amb
            {
                public static implicit operator int(Amb a) => 10;
                public static implicit operator string(Amb a) => "zz";
            }
            static class Helper
            {
                public static void Consume(int x, ref int count, params int[] extras) => count += x;
                public static void Consume(string x, ref int count, params string[] extras) => count += x.Length;
            }
            class C
            {
                public static void M()
                {
                    Amb a = new Amb();
                    int count = 0;
                    Helper.Consume(a, ref count);
                    System.Console.WriteLine(count);
                }
            }
            """;

        var result = Run(src);

        // Both declarations become ByRef (2 occurrences); no third ByRef slot
        // may be synthesized at the call site.
        var callLine = result.OutputCode.Split('\n')
            .First(l => l.TrimStart().StartsWith("Helper.Consume("));
        Assert.Contains("ref count", callLine);
        Assert.Equal(2, new Regex(@"ByRef<int>").Matches(result.OutputCode).Count);
    }
}
