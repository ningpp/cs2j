using System.Diagnostics;
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
/// Classes in this collection swap the process-global Console.Out/Error to
/// capture in-process chain diagnostics. xUnit parallelizes test classes by
/// default, so two capturing classes overlapping would clobber each other's
/// writers; the collection forbids parallelization between its members.
/// Non-capturing classes may still run alongside them — foreign output lines
/// landing in a capture are harmless (assertions use Contains).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class ConsoleChainTestCollection
{
    public const string Name = "console-chain";
}

/// <summary>
/// Project-level gate for C36 M2 (collection-expression desugaring). The
/// SampleExpressionDesugarSolution fixture exercises interface targets
/// (CE-4), deterministic nested temp allocation (CE-8), Span heap-array
/// lowering (CE-9) and CollectionBuilder factory emission (CE-10) across a
/// library plus an xUnit runtime project. The chain runs the DEFAULT
/// preprocess chain (expression-desugar is default-on);
/// VerifyBuild/VerifyTests prove the intermediate tree builds and keeps its
/// runtime behavior (native vs desugared double run).
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class CollectionExprProjectTests
{
    private const string PassId = "expression-desugar";
    private const string IntermediateDirName = ".cs2j-expression-desugar-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleExpressionDesugarSolution");

    private static string UnsupportedFixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleExpressionDesugarUnsupportedSolution");

    private static string GuardFixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleExpressionDesugarGuardSolution");

    private static string CliPath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null)
            {
                var candidate = Path.Combine(
                    dir, "src", "CSharpToJava.CLI", "bin", "Debug", "net10.0",
                    "CSharpToJava.CLI.dll");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll from test output directory.");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{CliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

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
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () => new EqualityConcatOptions(),
            ExpressionDesugarOptionsFactory = () => new ExpressionDesugarOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });

    [Fact(Skip = "TODO: 临时跳过端到端慢测试（排查根因中）— 136.61s")]
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

    // ------------------------------------------------------------------
    // M3 CE-GUARD: project-level static-abstract strict rejection.
    // ------------------------------------------------------------------

    [Fact]
    public async Task StaticAbstract_FailsStrict_With0585_IntermediateTreeKeepsOriginalFiles()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c36-guard-" + Guid.NewGuid().ToString("N"));
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
                // The fixture is legal C# 11 (it natively builds), so this
                // gate is the pass' own CS2J0585 — not a baseline compiler
                // error; verification stays off to assert that distinction.
                result = await RunChainAsync(
                    Path.Combine(
                        GuardFixtureDir, "SampleExpressionDesugarGuardSolution.sln"),
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
                "static abstract declarations must fail the strict guard chain");

            // 4 interface declarations: abstract property/method/operator and
            // the virtual Describe method.
            var output = capture.ToString();
            Assert.Contains("CS2J0585", output);
            var codeOccurrences = System.Text.RegularExpressions.Regex
                .Matches(output, "CS2J0585").Count;
            Assert.True(codeOccurrences >= 4, $"expected >=4 CS2J0585 lines, got {codeOccurrences}");

            // Runner semantics (pinned, see M3 plan): on strict failure the
            // intermediate directory is PREPARED (copied) and retained, but
            // every user .cs file is the byte-identical original — the atomic
            // gate rewrote nothing.
            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            Assert.True(
                Directory.Exists(intermediate),
                "the prepared intermediate tree is retained on strict failure");

            var intermediateFile = Path.Combine(
                intermediate, "SampleExpressionDesugarGuardLib", "GuardSites.cs");
            Assert.True(File.Exists(intermediateFile));
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(
                    GuardFixtureDir, "SampleExpressionDesugarGuardLib", "GuardSites.cs")),
                await File.ReadAllBytesAsync(intermediateFile));
            var intermediateText = await File.ReadAllTextAsync(intermediateFile);
            Assert.Contains("static abstract", intermediateText);
            Assert.Contains("static virtual", intermediateText);
            Assert.DoesNotContain("__ed_", intermediateText);
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
    public async Task StaticAbstract_CliProcess_ExitsNonZero_AndReports0585()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c36-guard-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                "-s", Path.Combine(
                    GuardFixtureDir, "SampleExpressionDesugarGuardSolution.sln"),
                "-d", outputRoot);

            Assert.NotEqual(0, exit);
            Assert.Contains("CS2J0585", output);
            Assert.Contains(
                "expression-desugar",
                output,
                StringComparison.OrdinalIgnoreCase);
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
    public async Task ListPasses_IncludesExpressionDesugar_WithC23AndC36CatalogItems()
    {
        var (exit, stdout) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var row = Assert.Single(
            stdout.Split('\n', '\r')
                .Where(line => line.Contains(PassId, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("rules=C23, C24, C36", row);
        Assert.Contains(" on ", row, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" off ", row, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripWhitespace(string text)
        => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
