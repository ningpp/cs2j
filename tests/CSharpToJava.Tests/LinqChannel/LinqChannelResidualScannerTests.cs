using CSharpToJava.Core.LinqChannel;
using Microsoft.CodeAnalysis;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// Core-level pins for the M3 §7.10 AfterFiles residual scanner
/// (<see cref="LinqChannelResidualScanner"/>): the preserved-call stream gate
/// (② Queryable calls untouched, ③ quoted-region calls untouched node for
/// node) and the helper-placement gate (④ procedural helpers only land in the
/// type that owned the expanded chain). Gates ①/⑤ (second-pass fixed point)
/// are driven by the adapter and pinned by
/// <see cref="LinqChannelIdempotencyResidualTests"/> plus the ProjectTests
/// fixture double-run.
/// </summary>
public sealed class LinqChannelResidualScannerTests
{
    private const string MixedSource = """
        using System.Linq;

        public static class ResidualHost
        {
            // Expandable Enumerable chain (produces a procedural helper).
            public static int[] Evens(int[] s)
                => s.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();

            // Frozen Queryable chain with a quoted lambda.
            public static object Queried(System.Linq.IQueryable<int> q)
                => q.Where(x => x > 0).Select(x => x + 1);
        }

        public static class NoLinqHere
        {
            public static int Plain(int x) => x + 1;
        }
        """;

    private static readonly (SemanticModel Model, SyntaxNode Root, string Text) RealOutput = BuildRealOutput();

    private static (SemanticModel Model, SyntaxNode Root, string Text) BuildRealOutput()
    {
        var run = LinqChannelDualPathHarness.RunBothPaths(MixedSource);
        var text = run.Channel.RewrittenRoot.ToFullString();
        var (model, root) = LinqChannelDualPathHarness.Recompile(text);
        return (model, root, text);
    }

    private static (SemanticModel InputModel, SyntaxNode InputRoot) Input()
    {
        var run = LinqChannelDualPathHarness.RunBothPaths(MixedSource);
        return (run.Model, run.PostDesugarRoot);
    }

    [Fact]
    public void RealChannelOutput_HasZeroResidualFindings()
    {
        var (inputModel, inputRoot) = Input();
        var findings = LinqChannelResidualScanner.ScanFile(
            inputModel, inputRoot,
            RealOutput.Model, RealOutput.Root,
            filePath: "ResidualHost.cs", strict: false);

        Assert.Empty(findings);
    }

    [Fact]
    public void TamperedOutput_QueryableCallDropped_FlagsLc0040_StreamMismatch()
    {
        var (inputModel, inputRoot) = Input();

        // A rewrite that swallowed the preserved Queryable node: the call
        // expression is replaced by its bare receiver (still compiles).
        var tampered = RealOutput.Text.Replace("q.Where(x => x > 0).Select(x => x + 1)", "q");
        var (outModel, outRoot) = LinqChannelDualPathHarness.Recompile(tampered);

        var lenient = LinqChannelResidualScanner.ScanFile(
            inputModel, inputRoot, outModel, outRoot, "ResidualHost.cs", strict: false);
        var mismatch = Assert.Single(lenient);
        Assert.Equal(LinqChannelDiagnosticCodes.ResidualMismatch, mismatch.Code);
        Assert.Equal(LinqChannelDiagnosticSeverity.Warning, mismatch.Severity);

        var strict = LinqChannelResidualScanner.ScanFile(
            inputModel, inputRoot, outModel, outRoot, "ResidualHost.cs", strict: true);
        Assert.All(strict, d => Assert.Equal(LinqChannelDiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public void TamperedOutput_HelperInjectedIntoTypeWithoutChain_FlagsLc0040_HelperPlacement()
    {
        var (inputModel, inputRoot) = Input();

        // A helper-shaped method lands in the type that owned no chain.
        var tampered = RealOutput.Text.Replace(
            "public static int Plain(int x) => x + 1;",
            "public static int Plain(int x) => x + 1;\n"
            + "        private static void Spurious_ProceduralLinq9() { }");
        var (outModel, outRoot) = LinqChannelDualPathHarness.Recompile(tampered);

        var findings = LinqChannelResidualScanner.ScanFile(
            inputModel, inputRoot, outModel, outRoot, "ResidualHost.cs", strict: false);

        var placement = Assert.Single(findings);
        Assert.Equal(LinqChannelDiagnosticCodes.ResidualMismatch, placement.Code);
        Assert.Contains("NoLinqHere", placement.Message);
    }

    [Fact]
    public void QuotedRegionStream_IsIdentifiedAndCompared()
    {
        // A quoted consumer with an inner Enumerable chain: the inner call is
        // part of the preserved stream even though its host is Enumerable.
        const string source = """
            using System.Linq;
            using System.Linq.Expressions;

            public static class TreeConsumer
            {
                public static void Consume(Expression<System.Func<int, bool>> predicate) { }
            }

            public static class QuoteHost
            {
                private static readonly int[] Numbers = [1, 2, 3];
                public static void Use()
                    => TreeConsumer.Consume(x => Numbers.Where(n => n > x).Any());
            }
            """;

        var run = LinqChannelDualPathHarness.RunBothPaths(source);
        var (outModel, outRoot) =
            LinqChannelDualPathHarness.Recompile(run.Channel.RewrittenRoot.ToFullString());

        var findings = LinqChannelResidualScanner.ScanFile(
            run.Model, run.PostDesugarRoot, outModel, outRoot, "QuoteHost.cs", strict: false);
        Assert.Empty(findings);

        // Replace the whole frozen quoted body (Where+Any) with a constant:
        // the replacement still compiles, but the quoted stream lost both
        // sites and the scanner flags it.
        var tampered = run.Channel.RewrittenRoot.ToFullString()
            .Replace("Numbers.Where(n => n > x).Any()", "true");
        var (tamperedModel, tamperedRoot) = LinqChannelDualPathHarness.Recompile(tampered);
        var broken = LinqChannelResidualScanner.ScanFile(
            run.Model, run.PostDesugarRoot, tamperedModel, tamperedRoot, "QuoteHost.cs", strict: false);
        Assert.Contains(broken, d => d.Code == LinqChannelDiagnosticCodes.ResidualMismatch);
    }
}
