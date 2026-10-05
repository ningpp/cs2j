using System.Diagnostics;
using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C26 gate project-level coverage (kept from M2, updated for M3):
/// <list type="bullet">
/// <item>the §5.3 single-point suppression predicate (channel intermediate
/// present ⇒ embedded E16 rewrite off; absent/null ⇒ untouched);</item>
/// <item>gate intercept at the real adapter level on a project with an
/// Enumerable chain nested inside a Queryable operator's quoted lambda:
/// the chain is frozen verbatim (M3: by the quoted-region gate, not just the
/// outer subtree freeze) and passes Release build verification;</item>
/// <item>the convert-project CLI double-run wiring end to end: with
/// --enable-pass linq-channel the Java conversion emits no embedded
/// cs2j-linq-report.json (E16 stats null), without the channel it does.</item>
/// </list>
/// The M2 observe-only companion test was removed in M3: the
/// <c>EnableQueryableGate</c> off-switch no longer exists; the ungated E16
/// fallback path is pinned instead by the null-gate Core unit test.
/// </summary>
public sealed class LinqChannelGateM2ProjectTests
{
    private const string PassId = "linq-channel";
    private const string IntermediateDirName = ".cs2j-linq-channel-src";
    private const string ChannelReportFileName = "cs2j-linq-channel-report.json";
    private const string EmbeddedReportFileName = "cs2j-linq-report.json";

    private static readonly HashSet<string> EmptySet =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ChannelSet =
        new(new[] { IntermediateDirName }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SuppressionPredicate_FollowsExecutedIntermediateSetExactly()
    {
        Assert.False(CliPreprocessChain.IsEmbeddedLinqRewriteSuppressed(null));
        Assert.False(CliPreprocessChain.IsEmbeddedLinqRewriteSuppressed(EmptySet));
        Assert.False(CliPreprocessChain.IsEmbeddedLinqRewriteSuppressed(
            new HashSet<string>(new[] { ".cs2j-foreach-source-src" },
                StringComparer.OrdinalIgnoreCase)));
        Assert.True(CliPreprocessChain.IsEmbeddedLinqRewriteSuppressed(ChannelSet));
        // OrdinalIgnoreCase contract (paths are compared case-insensitively).
        Assert.True(CliPreprocessChain.IsEmbeddedLinqRewriteSuppressed(
            new HashSet<string>(new[] { ".CS2J-LINQ-CHANNEL-SRC" },
                StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task InterceptGate_NestedEnumerableInQuotedLambda_PassesBuildVerification()
    {
        var (tempRoot, sourceDir, outputDir) = CreateWorkspace();
        try
        {
            var errorCapture = new StringWriter();
            var originalError = Console.Error;
            CliChainResult result;
            try
            {
                Console.SetError(errorCapture);
                result = await RunChainAsync(sourceDir, outputDir,
                    () => new LinqChannelOptions());
            }
            finally
            {
                Console.SetError(originalError);
            }

            Assert.True(
                result.Success,
                "the M3 gate freezes the Queryable chain including the " +
                "Enumerable chain nested in its quoted lambda; Release build must pass.\nstderr:\n"
                + errorCapture);
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);

            var producedOn = await File.ReadAllTextAsync(
                Path.Combine(outputDir, IntermediateDirName, "Tools.cs"));
            Assert.Contains("q.Where(x => new[] { 1, 2, 3 }.Where(n => n > x).Any())", producedOn);
            Assert.DoesNotContain("_ProceduralLinq", producedOn);

            var report = Path.Combine(outputDir, IntermediateDirName, ChannelReportFileName);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            var routing = doc.RootElement.GetProperty("routing");
            Assert.True(routing.GetProperty("queryableChainsPreserved").GetInt32() >= 1);
            // M3: the nested chain is no longer counted as expanded; it has
            // its own quoted-preserve counter.
            Assert.True(
                routing.GetProperty("expressionQuotedChainsPreserved").GetInt32() >= 1,
                "the Enumerable chain nested in the quoted lambda must be a quoted-preserved chain");
        }
        finally
        {
            DeleteQuiet(tempRoot);
        }
    }

    [Fact]
    public async Task ConvertProject_ChannelDoubleRun_SuppressesEmbeddedReport_AndWithoutChannelWritesIt()
    {
        var cli = LocateCli();
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c26-m2-cli-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "SampleProj");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "SampleProj.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\n" +
            "    <Nullable>enable</Nullable>\n" +
            "  </PropertyGroup>\n</Project>\n");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "Tools.cs"),
            """
            using System.Linq;

            namespace SampleProj;

            public static class Tools
            {
                public static int[] EvensDoubled(int[] src)
                    => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
            }
            """);

        var channelDest = Path.Combine(tempRoot, "java-channel");
        var plainDest = Path.Combine(tempRoot, "java-plain");
        try
        {
            // ---- channel run: gate expands Enumerable in the intermediate
            // tree; the downstream Java conversion must not run E16 again.
            var (channelExit, channelOut, channelErr) = await RunCliAsync(cli,
                "convert-project", "-s", sourceDir, "-d", channelDest,
                "--enable-pass", PassId,
                "--linq-report",
                "--no-verify-build", "--no-verify-tests");
            Assert.True(channelExit == 0,
                $"convert-project with linq-channel failed ({channelExit}).\nstderr: {channelErr}\nstdout tail: {Tail(channelOut)}");

            var channelIntermediate = Path.Combine(channelDest, IntermediateDirName);
            Assert.True(Directory.Exists(channelIntermediate),
                "the channel intermediate tree must be materialized under the destination root");
            var channelReport = Path.Combine(channelIntermediate, ChannelReportFileName);
            Assert.True(File.Exists(channelReport), "channel report missing in intermediate tree");
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(channelReport)))
            {
                Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.True(
                    doc.RootElement.GetProperty("routing")
                        .GetProperty("enumerableChainsExpanded").GetInt32() >= 1,
                    "the Enumerable chain must be expanded by the channel pass");
            }

            Assert.False(File.Exists(Path.Combine(channelDest, EmbeddedReportFileName)),
                "§5.3: when the channel ran, embedded E16 rewrite is suppressed, " +
                "so no cs2j-linq-report.json may be written by the Java conversion");
            Assert.True(
                Directory.EnumerateFiles(channelDest, "*.java", SearchOption.AllDirectories).Any(),
                "Java conversion output must still be produced from the channel intermediate tree");

            // ---- plain run: no channel, E16 runs inside the conversion and
            // writes its report exactly as before C26.
            var (plainExit, plainOut, plainErr) = await RunCliAsync(cli,
                "convert-project", "-s", sourceDir, "-d", plainDest,
                "--linq-report",
                "--no-verify-build", "--no-verify-tests");
            Assert.True(plainExit == 0,
                $"convert-project without channel failed ({plainExit}).\nstderr: {plainErr}\nstdout tail: {Tail(plainOut)}");
            var embeddedReport = Path.Combine(plainDest, EmbeddedReportFileName);
            Assert.True(File.Exists(embeddedReport),
                "without the channel, the embedded E16 pass must write its report as before");
            using var doc2 = JsonDocument.Parse(await File.ReadAllTextAsync(embeddedReport));
            // The embedded pass demonstrably ran over the LINQ source: the
            // exact rewrite-vs-skip split in the MSBuild pipeline is a
            // pre-existing E16 behavior (some shapes stream-fallback), so the
            // contract pinned here is stats presence, which the suppressed run
            // cannot produce (stats are null when the pass early-returns).
            var linqActivity =
                doc2.RootElement.GetProperty("rewrittenChainCount").GetInt32()
                + doc2.RootElement.GetProperty("skippedChainCount").GetInt32();
            Assert.True(linqActivity >= 1,
                "the embedded E16 report must show LINQ chain activity without the channel");
        }
        finally
        {
            DeleteQuiet(tempRoot);
        }
    }

    private static Task<CliChainResult> RunChainAsync(
        string sourceDir,
        string outputDir,
        Func<LinqChannelOptions> channelOptionsFactory)
    {
        var registry = PassCatalog.BuildRegistry();
        return CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = Path.Combine(sourceDir, "SampleLinqUse.csproj"),
            DestinationRoot = outputDir,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () =>
                new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () =>
                new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () =>
                new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            LinqChannelOptionsFactory = channelOptionsFactory,
            LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
            EnablePassIds = new[] { PassId },
            VerifyBuild = true,
            VerifyTests = false,
        });
    }

    private static (string TempRoot, string SourceDir, string OutputDir) CreateWorkspace()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c26-m2-gate-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);

        // The discriminating M0/M2 sample: an Enumerable chain nested inside
        // the quoted lambda of a Queryable operator.
        File.WriteAllText(Path.Combine(sourceDir, "Tools.cs"),
            """
            using System.Linq;

            namespace SampleLinqUse;

            public static class Tools
            {
                public static IQueryable<int> Query(IQueryable<int> q)
                    => q.Where(x => new[] { 1, 2, 3 }.Where(n => n > x).Any());
            }
            """);
        File.WriteAllText(Path.Combine(sourceDir, "SampleLinqUse.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n" +
            "    <ImplicitUsings>enable</ImplicitUsings>\n" +
            "    <Nullable>enable</Nullable>\n" +
            "    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>\n" +
            "  </PropertyGroup>\n</Project>\n");

        return (tempRoot, sourceDir, outputDir);
    }

    private static string LocateCli()
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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string cliPath, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{cliPath}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}",
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
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string Tail(string text)
        => text.Length <= 2000 ? text : text[^2000..];

    private static void DeleteQuiet(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort under parallel test execution.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
