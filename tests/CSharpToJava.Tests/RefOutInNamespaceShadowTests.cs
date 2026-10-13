using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using CSharpToJava.Core.RefOutInPreprocessor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Regression for the two residual CS1615 sites in the uri FunctionalTests
/// conversion (WebSocketsUriParserTest.Uri.TryCreate(..., out uri)). The
/// ref-out-in pass binds ONE semantic model over the WHOLE solution graph: a
/// sibling UnitTests source declares <c>namespace dotnet.system.Net.Test.Uri.*</c>,
/// which injects a namespace symbol named <c>Uri</c> into an enclosing lookup
/// scope of the call site (namespace <c>dotnet.system.Net.Test.Uris.*</c>).
/// C# name lookup then binds the static-call receiver <c>Uri</c> to that
/// NAMESPACE instead of the source type <c>dotnet.system.Uri</c> (CS0234):
/// GetSymbolInfo returns Symbol=null, CandidateReason=None and ZERO
/// candidates. The old rewriter only understood a unique symbol or
/// OverloadResolutionFailure, so the bare <c>out uri</c> survived while the
/// declaration was already ByRef — CS1615 in the project's own (unshadowed)
/// build.
///
/// Recovery replays enclosing-namespace lookup while ignoring namespace
/// symbols and lifts against the source type when every same-arity static
/// candidate is an internal source method agreeing on the byref shape.
/// </summary>
public class RefOutInNamespaceShadowTests
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
            }
            internal sealed class SlotByRef<T> : ByRef<T>
            {
                private T _slot = default!;
                public SlotByRef() { }
                public SlotByRef(T value) { _slot = value; }
                public override T Value { get => _slot; set => _slot = value; }
            }
        }
        """;

    // Production source: the type the per-project build binds the call to.
    private const string ProductionSource = """
        namespace A.B
        {
            public class Uri
            {
                public int Kind;
                public static bool TryCreate(string s, int kind, out Uri r)
                {
                    r = new Uri { Kind = kind + s.Length };
                    return true;
                }
                public static bool TryCreate(Uri b, string s, out Uri r)
                {
                    r = new Uri { Kind = b.Kind + s.Length };
                    return true;
                }
            }
        }
        """;

    // Sibling-project sources: the namespace names Uri/Guid shadow the type
    // names from any call site nested under A.B.Test.*.
    private const string ShadowSource = """
        namespace A.B.Test.Uri.Foreign
        {
            internal class UriShadowMarker { }
        }
        namespace A.B.Test.Guid.Foreign
        {
            internal class GuidShadowMarker { }
        }
        """;

    private const string CallerSource = """
        using System;
        namespace A.B.Test.Uris
        {
            class C
            {
                public static int M()
                {
                    Uri u;
                    Uri.TryCreate("ws", 7, out u);
                    return u.Kind;
                }
            }
        }
        """;

    private const string NoShadowCallerSource = """
        namespace A.B.Test2
        {
            class E
            {
                public static int M()
                {
                    Uri u;
                    Uri.TryCreate("z", 1, out u);
                    return u.Kind;
                }
            }
        }
        """;

    private const string GuidCallerSource = """
        using System;
        namespace A.B.Test.Uris
        {
            class G
            {
                public static bool M()
                {
                    Guid g;
                    return Guid.TryParse("not-a-guid", out g);
                }
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

    private static (CSharpCompilation Compilation, SyntaxTree Tree) BuildMerged(
        string callerSource,
        string callerName)
    {
        var trees = new[]
        {
            CSharpSyntaxTree.ParseText(ProductionSource, path: "prod.cs"),
            CSharpSyntaxTree.ParseText(ShadowSource, path: "shadow.cs"),
            CSharpSyntaxTree.ParseText(callerSource, path: callerName),
        };
        var compilation = CSharpCompilation.Create(
            "roi-shadow-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        return (compilation, trees[2]);
    }

    private static RefOutInRewriterResult Rewrite(SyntaxTree tree, CSharpCompilation compilation)
        => new RefOutInRewriter().Rewrite(tree.ToString(), compilation.GetSemanticModel(tree));

    private static int Invoke(IEnumerable<string> sources, bool withByRefStub, string typeFullName)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToList();
        if (withByRefStub) trees.Add(CSharpSyntaxTree.ParseText(ByRefSupportStub));
        var compilation = CSharpCompilation.Create(
            "roi-shadow-emit-" + Guid.NewGuid().ToString("N"),
            trees,
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success,
            "emit failed:\n" + string.Join("\n",
                emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var asm = AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(ms.ToArray()));
        var type = asm.GetType(typeFullName)!;
        return (int)type.GetMethod("M", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
    }

    [Fact]
    public void Namespace_Shadowed_Receiver_Out_Call_Is_Still_Lifted()
    {
        var (compilation, callerTree) = BuildMerged(CallerSource, "caller.cs");
        var model = compilation.GetSemanticModel(callerTree);

        // Lock the reproduction shape: receiver binds to a NAMESPACE and the
        // invocation has no symbol, no reason and no candidates.
        var invocation = callerTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();
        var info = model.GetSymbolInfo(invocation);
        Assert.Null(info.Symbol);
        Assert.Equal(CandidateReason.None, info.CandidateReason);
        Assert.Empty(info.CandidateSymbols);
        var receiver = ((MemberAccessExpressionSyntax)invocation.Expression).Expression;
        Assert.True(model.GetSymbolInfo(receiver).Symbol is INamespaceSymbol);

        var callerResult = Rewrite(callerTree, compilation);
        Assert.True(callerResult.Changed);
        Assert.DoesNotContain(callerResult.Diagnostics,
            d => d.Severity == RefOutInSeverity.Error);
        Assert.Contains("ByRef", callerResult.OutputCode);
        Assert.DoesNotMatch(new Regex(@"TryCreate\([^;]*\bout\s+u\b"),
            callerResult.OutputCode);

        // The declaration side is lifted as in the real pipeline.
        var prodTree = compilation.SyntaxTrees.Single(t => t.FilePath == "prod.cs");
        var prodResult = Rewrite(prodTree, compilation);
        Assert.Contains("ByRef<Uri>", prodResult.OutputCode);

        // Per-project view: the shadow namespace exists in no per-project
        // compilation, so the rewritten output must behave like the original.
        Assert.Equal(9, Invoke(new[] { ProductionSource, CallerSource }, false, "A.B.Test.Uris.C"));
        Assert.Equal(9, Invoke(
            new[] { prodResult.OutputCode, callerResult.OutputCode }, true, "A.B.Test.Uris.C"));
    }

    [Fact]
    public void Shadowed_Receiver_With_Metadata_Only_Target_Is_Not_Lifted()
    {
        var (compilation, callerTree) = BuildMerged(GuidCallerSource, "guidcaller.cs");
        var model = compilation.GetSemanticModel(callerTree);

        var invocation = callerTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();
        Assert.Null(model.GetSymbolInfo(invocation).Symbol);
        Assert.True(model.GetSymbolInfo(
            ((MemberAccessExpressionSyntax)invocation.Expression).Expression).Symbol
            is INamespaceSymbol);

        // The only Guid type is the framework metadata one; no source type
        // exists in any enclosing scope, so recovery must refuse to lift the
        // genuine external out call (Changed is not asserted: the pass also
        // reformats files it scans).
        var result = Rewrite(callerTree, compilation);
        Assert.DoesNotContain("ByRef", result.OutputCode);
        Assert.Contains("Guid.TryParse(\"not-a-guid\", out g)", result.OutputCode);
    }

    [Fact]
    public void Unshadowed_Framework_Out_Method_Is_Not_Lifted()
    {
        var tree = CSharpSyntaxTree.ParseText(GuidCallerSource, path: "g.cs");
        var compilation = CSharpCompilation.Create(
            "roi-shadow-bcl-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            GetReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var result = Rewrite(tree, compilation);
        Assert.DoesNotContain("ByRef", result.OutputCode);
        Assert.Contains("Guid.TryParse(\"not-a-guid\", out g)", result.OutputCode);
    }

    [Fact]
    public void Unshadowed_Source_Out_Call_Still_Lifted()
    {
        var (compilation, callerTree) = BuildMerged(NoShadowCallerSource, "noshadowcaller.cs");
        var model = compilation.GetSemanticModel(callerTree);

        // No shadow on this call site's namespace chain: unique source symbol
        // via the pre-existing path.
        var invocation = callerTree.GetRoot().DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Single();
        var method = Assert.IsAssignableFrom<IMethodSymbol>(model.GetSymbolInfo(invocation).Symbol);
        Assert.True(method.Locations.Any(l => l.IsInSource));

        var callerResult = Rewrite(callerTree, compilation);
        Assert.True(callerResult.Changed);
        Assert.Contains("ByRef", callerResult.OutputCode);

        var prodTree = compilation.SyntaxTrees.Single(t => t.FilePath == "prod.cs");
        var prodResult = Rewrite(prodTree, compilation);
        Assert.Equal(2, Invoke(
            new[] { prodResult.OutputCode, callerResult.OutputCode }, true, "A.B.Test2.E"));
    }
}
