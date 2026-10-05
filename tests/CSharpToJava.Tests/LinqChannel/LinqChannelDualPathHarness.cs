using System.Text.RegularExpressions;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.LinqRewrite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CSharpToJava.Tests.LinqChannel;

/// <summary>
/// Shared dual-path harness for C26 channel equivalence tests (M1/M2):
/// compiles a snippet once, runs the shared stage-1 query desugar, rebuilds
/// the semantic model over the post-desugar tree, and feeds the identical
/// input through
/// <list type="bullet">
/// <item>the embedded E16 call shape (<c>new LinqRewriter(model)</c>, no gate),
/// and</item>
/// <item>the channel (<see cref="LinqChannelRewriter.ProcessFile"/> with the
/// supplied <see cref="LinqChannelOptions"/> — intercept mode by default in
/// M2).</item>
/// </list>
/// </summary>
internal static class LinqChannelDualPathHarness
{
    public static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Collections.Generic.HashSet<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Queryable).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        MetadataReference.CreateFromFile(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Collections.dll")),
    };

    private static readonly Regex GeneratedHelperName =
        new(@"[A-Za-z_][A-Za-z0-9_]*_ProceduralLinq\w*", RegexOptions.Compiled);

    public sealed record DualPathResult(
        SyntaxNode PostDesugarRoot,
        SyntaxNode EmbeddedRoot,
        LinqRewriteStatistics EmbeddedStatistics,
        LinqChannelRewriteResult Channel,
        SemanticModel Model)
    {
        public string EmbeddedText => Normalize(EmbeddedRoot);

        public string ChannelText => Normalize(Channel.RewrittenRoot);
    }

    public static DualPathResult RunBothPaths(
        string source,
        LinqChannelOptions? options = null,
        ConversionOptions? conversionOptions = null)
    {
        var originalTree = CSharpSyntaxTree.ParseText(
            source, new CSharpParseOptions(LanguageVersion.Latest));
        var originalCompilation = CSharpCompilation.Create(
            "c26-equiv-" + Guid.NewGuid().ToString("N"),
            new[] { originalTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        Assert.Empty(originalCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error));

        // Stage 1: query desugaring (single shared implementation).
        var (desugaredRoot, _) = LinqChannelRewriter.DesugarQueries(originalTree.GetRoot());
        var desugaredText = desugaredRoot.ToFullString();

        var desugaredTree = CSharpSyntaxTree.ParseText(
            desugaredText,
            new CSharpParseOptions(LanguageVersion.Latest),
            path: originalTree.FilePath);
        var compilation = CSharpCompilation.Create(
            "c26-equiv-" + Guid.NewGuid().ToString("N"),
            new[] { desugaredTree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var desugaredErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(desugaredErrors.Count == 0,
            "desugared snippet must compile: " + string.Join("; ", desugaredErrors));
        var model = compilation.GetSemanticModel(desugaredTree);
        var postDesugarRoot = desugaredTree.GetRoot();

        // E16 embedded call shape — no gate (the gate constructor argument is
        // optional and defaults to null for every existing call site).
        var embeddedRewriter = new LinqRewriter(model, conversionOptions);
        var embeddedRoot = embeddedRewriter.Visit(postDesugarRoot) ?? postDesugarRoot;

        // Channel call shape — plan + switchable routing gate.
        var channelResult = new LinqChannelRewriter(options ?? new LinqChannelOptions())
            .ProcessFile(model, postDesugarRoot, filePath: null, conversionOptions);

        return new DualPathResult(
            postDesugarRoot, embeddedRoot, embeddedRewriter.Statistics,
            channelResult, model);
    }

    public static string Normalize(Microsoft.CodeAnalysis.SyntaxNode root)
    {
        var normalized = root.NormalizeWhitespace().ToFullString();
        return GeneratedHelperName.Replace(normalized, "_ProceduralLinqHelper");
    }

    /// <summary>
    /// Recompiles output text standalone (nullable enabled) and returns the
    /// fresh semantic model + root. Fails the test on the first error.
    /// </summary>
    public static (SemanticModel Model, SyntaxNode Root) Recompile(string outputText)
    {
        var tree = CSharpSyntaxTree.ParseText(
            outputText, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "c26-equiv-re-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "rewritten output must compile: " + string.Join("; ", errors.Take(5)));
        return (compilation.GetSemanticModel(tree), tree.GetRoot());
    }

    /// <summary>
    /// Recompiles rewritten output standalone (nullable enabled) and fails the
    /// test on the first error: every dual-path matrix output must stay valid
    /// C# (M2 residual surface, design §7.8).
    /// </summary>
    public static void AssertOutputCompiles(string outputText)
    {
        Recompile(outputText);
    }
}
