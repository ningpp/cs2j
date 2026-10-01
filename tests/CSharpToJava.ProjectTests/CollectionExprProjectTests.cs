using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.EqualityConcat;
using CSharpToJava.Core.ExpressionDesugar;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C36 M2 (collection-expression desugaring). The
/// SampleExpressionDesugarSolution fixture exercises interface targets
/// (CE-4), deterministic nested temp allocation (CE-8), Span heap-array
/// lowering (CE-9) and CollectionBuilder factory emission (CE-10) across a
/// library plus an xUnit runtime project. The chain runs the DEFAULT
/// preprocess chain with <c>--enable-pass expression-desugar</c>;
/// VerifyBuild/VerifyTests prove the intermediate tree builds and keeps its
/// runtime behavior (native vs desugared double run).
/// </summary>
public class CollectionExprProjectTests
{
    private const string PassId = "expression-desugar";
    private const string IntermediateDirName = ".cs2j-expression-desugar-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleExpressionDesugarSolution");

    private static string UnsupportedFixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleExpressionDesugarUnsupportedSolution");

    private static Task<CliChainResult> RunChainAsync(
        string sourcePath, string outputRoot, bool verify)
        => CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = sourcePath,
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
            ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });

    [Fact]
    public async Task Fixture_DefaultChainPlusPass_Builds_TestsPass_NoResidualSyntax()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-expression-desugar");
        try
        {
            var capture = new StringWriter();
            var oldOut = Console.Out;
            var oldError = Console.Error;
            Console.SetOut(capture);
            Console.SetError(capture);

            CliChainResult result;
            try
            {
                result = await RunChainAsync(
                    Path.Combine(FixtureDir, "SampleExpressionDesugarSolution.sln"),
                    outputRoot,
                    verify: true);
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldError);
            }

            Assert.True(
                result.Success,
                "expression-desugar chain failed. captured chain output:\n" + capture);

            // The expression-desugar stage must be in the executed plan.
            Assert.Contains(
                IntermediateDirName,
                result.ExecutedIntermediateDirectoryNames);

            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            Assert.True(
                Directory.Exists(intermediate),
                $"missing intermediate tree: {intermediate}");

            // Residual gate: no collection-expression / spread syntax survives
            // in any user file of the intermediate tree. Cs2j* support
            // projects are tooling-released and are not fixture input.
            foreach (var file in Directory.EnumerateFiles(
                         intermediate, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(intermediate, file);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Any(p => p.StartsWith("Cs2j", StringComparison.Ordinal)))
                {
                    continue;
                }

                var root = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file))
                    .GetRoot();
                Assert.Empty(root.DescendantNodes().OfType<CollectionExpressionSyntax>());
                Assert.Empty(root.DescendantNodes().OfType<SpreadElementSyntax>());
            }

            var builderSource = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleExpressionDesugarLib", "BuilderTypes.cs"));
            var builderNormalized = StripWhitespace(builderSource);

            // CE-10: no-spread factory call with an inline heap-array argument.
            Assert.Contains(
                StripWhitespace("global::SampleExpressionDesugarLib.MyBuiltBuilder.Create("
                    + "new int[] { 1, 2, 3 })"),
                builderNormalized);
            // CE-10: empty same-shape call.
            Assert.Contains(
                StripWhitespace("global::SampleExpressionDesugarLib.MyBuiltBuilder.Create("
                    + "new int[] { })"),
                builderNormalized);
            // CE-10: generic BCL factory keeps explicit type arguments.
            Assert.Contains(
                StripWhitespace("global::System.Collections.Immutable.ImmutableArray"
                    + ".Create<int>("),
                builderNormalized);

            var interfaceSource = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleExpressionDesugarLib", "InterfaceTargets.cs"));
            var interfaceNormalized = StripWhitespace(interfaceSource);
            // CE-4: interface target -> zero-temp List<T> initializer.
            Assert.Contains(
                StripWhitespace("new global::System.Collections.Generic.List<int>() { 1, 2, 3 }"),
                interfaceNormalized);
            // The five interface sites are plain List assignments, never ToArray.
            Assert.DoesNotContain(".ToArray()", interfaceSource);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UnsupportedSites_FailStrict_With0580And0581()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c36-unsupported-" + Guid.NewGuid().ToString("N"));
        try
        {
            // This fixture does not baseline-compile (CS9187/CS9174), so build
            // and test verification stay off: the gate is the pass diagnostics.
            var capture = new StringWriter();
            var oldOut = Console.Out;
            var oldError = Console.Error;
            Console.SetOut(capture);
            Console.SetError(capture);

            CliChainResult result;
            try
            {
                result = await RunChainAsync(
                    Path.Combine(
                        UnsupportedFixtureDir,
                        "SampleExpressionDesugarUnsupportedSolution.sln"),
                    outputRoot,
                    verify: false);
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldError);
            }

            Assert.False(
                result.Success,
                "unresolvable builder / table-out interface must fail the strict chain");
            Assert.Contains("CS2J0580", capture.ToString());
            Assert.Contains("CS2J0581", capture.ToString());
            Assert.Contains("CS9174", capture.ToString());
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    private static string StripWhitespace(string text)
        => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
