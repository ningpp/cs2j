// C26 M0 probe ③: minimal reproduction of the LinqRewriter syntax-name
// fallback hole (design §1.3, R2, acceptance §8-1):
//
//   When the semantic model fails to bind a Queryable-shaped call,
//   LinqRewriter.GetMethodFullName (LinqRewriter.cs L796-L858 on the M0 base)
//   fabricates an IEnumerable<TSource> identity purely from the syntax token
//   ("Where"/"Select"/…), so IsSupportedMethod returns true and the embedded
//   E16 rewriter treats the call as Enumerable and proceduralizes it —
//   exactly the silent IQueryable client-evaluation hazard catalog risk 17
//   describes.
//
// This test pins:
//   1. a realistic "missing metadata / error-type receiver" shape produces a
//      null symbol with the observed CandidateReason (recorded below);
//   2. the prototype classifier returns Ambiguous on that shape, so under the
//      C26 gate the name-guess fallback becomes unreachable (node preserved +
//      LC0010 instead of guessed Enumerable);
//   3. today's private GetMethodFullName on that same node nevertheless
//      returns the fabricated IEnumerable identity — the vulnerability pinned
//      in code, independent of whether downstream rule expansion succeeds or
//      throws into the skip-chain catch;
//   4. the observed end-to-end behavior of the current rewriter on the shape
//      (rewritten vs skipped), recorded honestly.

using System.Reflection;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel.Probe;

public sealed class LinqRouteMisExpansionProbeTests
{
    private readonly ITestOutputHelper _output;

    public LinqRouteMisExpansionProbeTests(ITestOutputHelper output) => _output = output;

    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    };

    // Receiver type lives in an assembly/namespace that is NOT referenced —
    // the "incomplete metadata" shape the design names as the LC0010 source.
    private const string UnboundQueryableStyleSource = """
        using System;

        public static class Repro
        {
            public static void Chain(global::Missing.Metadata.IDbSet<int> db)
            {
                var q = db.Where(x => x > 0).Select(x => x + 1);
                Console.WriteLine(q);
            }

            public static void ForeachOver(global::Missing.Metadata.IDbSet<int> db)
            {
                foreach (var item in db.Where(x => x > 0))
                {
                    Console.WriteLine(item);
                }
            }
        }
        """;

    private static (CSharpCompilation Compilation, SemanticModel Model) CompileBroken()
    {
        var tree = CSharpSyntaxTree.ParseText(UnboundQueryableStyleSource,
            new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-misexpand-probe-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return (compilation, compilation.GetSemanticModel(tree));
    }

    private static InvocationExpressionSyntax FindInvocation(SyntaxNode root, string methodName, string enclosingMethod)
        => root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == enclosingMethod)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(n => (n.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == methodName);

    private static string? InvokeGetMethodFullName(LinqRewriter rewriter, InvocationExpressionSyntax invocation)
        => (string?)typeof(LinqRewriter)
            .GetMethod("GetMethodFullName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(rewriter, new object[] { invocation });

    [Fact]
    public void UnboundQueryableStyleCall_SymbolIsNull_ClassifierReturnsAmbiguous()
    {
        var (compilation, model) = CompileBroken();

        // The source intentionally does not compile: the receiver type is
        // missing metadata. Record the diagnostics honestly.
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()}")
            .ToList();
        _output.WriteLine("compile errors (expected): " + string.Join(" | ", errors));
        Assert.NotEmpty(errors);

        foreach (var methodName in new[] { "Where", "Select" })
        {
            var node = FindInvocation(model.SyntaxTree.GetCompilationUnitRoot(), methodName, "Chain");
            var info = model.GetSymbolInfo(node);
            _output.WriteLine($"{methodName}: Symbol={info.Symbol?.ToString() ?? "<null>"}, " +
                $"CandidateReason={info.CandidateReason}, candidates={info.CandidateSymbols.Length}");

            Assert.Null(info.Symbol);

            var decision = new LinqRouteClassifier(model).ClassifyInvocation(node);
            Assert.Equal(LinqRouteKind.Ambiguous, decision.Kind);
            // Pinned observation on ba7a0897: the error-type receiver yields
            // CandidateReason.None with zero candidates → Unbound (LC0010).
            Assert.Equal(LinqRouteAmbiguity.Unbound, decision.Ambiguity);
            Assert.Equal(CandidateReason.None, decision.CandidateReason);
            Assert.True(decision.IsPreserved);
        }
    }

    [Fact]
    public void Today_GetMethodFullName_FabricatesEnumerableIdentity_OnUnboundQueryableStyleCall()
    {
        // The vulnerability pin (design §1.3 concrete dangerous path):
        // with no semantic symbol, the private fallback at LinqRewriter.cs
        // L796-L858 guesses "System.Collections.Generic.IEnumerable<TSource>.Where(...)"
        // purely from the identifier text.
        var (_, model) = CompileBroken();
        var rewriter = new LinqRewriter(model, new ConversionOptions());

        var where = FindInvocation(model.SyntaxTree.GetCompilationUnitRoot(), "Where", "Chain");
        var fabricated = InvokeGetMethodFullName(rewriter, where);
        _output.WriteLine($"GetMethodFullName(Where) = {fabricated ?? "<null>"}");
        Assert.NotNull(fabricated);
        Assert.Equal(
            "System.Collections.Generic.IEnumerable<TSource>.Where<TSource>(System.Func<TSource, bool>)",
            fabricated);

        var select = FindInvocation(model.SyntaxTree.GetCompilationUnitRoot(), "Select", "Chain");
        var fabricatedSelect = InvokeGetMethodFullName(rewriter, select);
        _output.WriteLine($"GetMethodFullName(Select) = {fabricatedSelect ?? "<null>"}");
        Assert.Equal(
            "System.Collections.Generic.IEnumerable<TSource>.Select<TSource, TResult>(System.Func<TSource, TResult>)",
            fabricatedSelect);

        // And the fabricated identity passes the rewriter's own support gate —
        // the branch that must become unreachable once the C26 gate runs first.
        var isSupported = typeof(LinqRewriter)
            .GetMethod("IsSupportedMethod", BindingFlags.Instance | BindingFlags.NonPublic, null,
                new[] { typeof(InvocationExpressionSyntax) }, null)!;
        foreach (var node in new[] { where, select })
        {
            var supported = (bool)isSupported.Invoke(rewriter, new object[] { node })!;
            _output.WriteLine($"IsSupportedMethod({(node.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text}) = {supported}");
            Assert.True(supported,
                "fabricated name identity passes the Enumerable support gate — hole reachable today");
        }
    }

    [Fact]
    public void Today_EndToEndRewrite_RecordsObservedOutcome_OnUnboundShape()
    {
        // Reachability analysis for M1 (honest observation, not a behavioral
        // promise): run the current rewriter over the unbound Queryable-style
        // source. Two outcomes are possible in principle:
        //   (a) the chain is proceduralized (full mis-expansion), or
        //   (b) a later semantic cast throws and the wide catch records a
        //       skipped chain after the identity was already mis-guessed.
        // Either way the unsafe branch was entered (proven by the previous
        // test); this test records which outcome the current base exhibits so
        // M3's LC0011 invariant can target the real path.
        var (_, model) = CompileBroken();
        var rewriter = new LinqRewriter(model, new ConversionOptions());

        var rewrittenRoot = rewriter.Visit(model.SyntaxTree.GetCompilationUnitRoot());

        // Inspect ONLY the Chain method body for proceduralization markers;
        // the source itself already contains a foreach in ForeachOver, so a
        // whole-tree text check would be contaminated.
        var chainMethod = rewrittenRoot.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == "Chain");
        var chainText = chainMethod.ToFullString();
        var proceduralized = chainText.Contains("_linqitems") || chainText.Contains("foreach");
        _output.WriteLine($"RewrittenLinqQueries={rewriter.RewrittenLinqQueries}");
        _output.WriteLine($"SkippedLinqChains=[{string.Join(" | ", rewriter.SkippedLinqChains)}]");
        _output.WriteLine($"Chain method procedural markers present={proceduralized}");
        _output.WriteLine("Chain method text:\n" + chainText);

        // OBSERVED ON ba7a0897 (pinned, not assumed): the missing-metadata
        // shape enters the name-guess fallback but the rule expansion throws
        // NotSupportedException ("Specified method is not supported" — Roslyn
        // refuses semantic answers on the error-typed lambda), so the wide
        // catch records skipped chains and no foreach survives: this particular
        // shape lands in outcome (b), NOT in a shipped mis-expansion. The
        // dangerous identity fabrication itself is proven by the previous test
        // and is reachable independent of the catch outcome; project-pipeline
        // shapes where symbols partially resolve (the design's documented
        // "common in project pipeline" case) can land in outcome (a), which is
        // what M3's LC0011 invariant must close.
        Assert.Equal(0, rewriter.RewrittenLinqQueries);
        Assert.True(rewriter.SkippedLinqChains.Count > 0);
        Assert.All(rewriter.SkippedLinqChains, s => Assert.Contains("NotSupportedException", s));
        Assert.False(proceduralized,
            "Chain method unexpectedly proceduralized on this base — re-run the probe and " +
            "update the M0 reachability conclusion in the C26 design");

        // Contrast: with the prototype gate in front, the node is Ambiguous
        // and must be preserved byte-for-byte (no rewrite decision attempted).
        var where = FindInvocation(model.SyntaxTree.GetCompilationUnitRoot(), "Where", "Chain");
        var decision = new LinqRouteClassifier(model).ClassifyInvocation(where);
        Assert.Equal(LinqRouteKind.Ambiguous, decision.Kind);
        Assert.Equal(LinqRouteAmbiguity.Unbound, decision.Ambiguity);
        Assert.Equal(CandidateReason.None, decision.CandidateReason);
    }
}
