using System.Diagnostics;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// M3/M4 CLI/end-to-end gates for the C26 linq-channel pass (design §7.11):
/// catalog listing (Implemented, default-on after the M4 flip), default-chain
/// reachability plus the conflicting enable/disable rejection, verify-build
/// failure exit code, strict LC0012 escalation to a non-zero exit, and the
/// LC0011 whole-file rollback path driven through the real
/// <see cref="CliPreprocessChain"/> via the adapter's test-only fault seam.
/// </summary>
public sealed class LinqChannelM3CliProjectTests
{
    private const string PassId = "linq-channel";
    private const string IntermediateDirName = ".cs2j-linq-channel-src";
    private const string ReportFileName = "cs2j-linq-channel-report.json";

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

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(params string[] args)
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
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateProject(string csprojName, string csharpSource)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2j-c26-m3-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, csprojName), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Class1.cs"), csharpSource);
        return dir;
    }

    // ---- catalog / reachability ----------------------------------------------

    [Fact]
    public async Task ListPasses_ShowsLinqChannel_Implemented_AndDefaultOn()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        // Match the id at row start: neighbors' run-after column can also
        // reference linq-channel.
        var line = Assert.Single(
            stdout.Split('\n'),
            l => l.TrimStart().StartsWith("linq-channel", StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        // Default column renders "on" padded to width; it must not render off.
        Assert.Contains(" on ", line);
        Assert.DoesNotContain(" off ", line);
        Assert.DoesNotContain("Stub", line);
        Assert.Contains("C26", line);
    }

    [Fact]
    public async Task ExplicitPass_RunsChannel_WritesReport_AndExitsZero()
    {
        var dir = CreateProject("Sample.csproj", """
            using System.Linq;
            public static class Tools
            {
                public static int[] Evens(int[] src)
                    => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
            }
            """);
        var outDir = Path.Combine(dir, "out");
        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", PassId,
                "-s", Path.Combine(dir, "Sample.csproj"),
                "-d", outDir,
                "--no-verify-tests");

            Assert.True(exit == 0, "linq-channel chain must succeed:\n" + stderr);
            Assert.True(
                File.Exists(Path.Combine(outDir, IntermediateDirName, ReportFileName)),
                "channel report must be materialized");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SlowE2EFact(50.9)]
    public async Task DefaultChain_RunsChannel_AndConflictingToggleRejected()
    {
        var dir = CreateProject("Sample.csproj", "public static class C { public static int F(int x) => x + 1; }");
        var outDefault = Path.Combine(dir, "out-default");
        var outConflict = Path.Combine(dir, "out-conflict");
        try
        {
            // M4 (2026-10-06): linq-channel is in the default chain — no
            // explicit --enable-pass needed for the tree to materialize.
            var (defaultExit, _, defaultErr) = await RunCliAsync(
                "preprocess",
                "-s", Path.Combine(dir, "Sample.csproj"),
                "-d", outDefault,
                "--no-verify-build", "--no-verify-tests");
            Assert.True(defaultExit == 0, defaultErr);
            Assert.True(
                Directory.Exists(Path.Combine(outDefault, IntermediateDirName)),
                "the default chain must materialize the linq-channel tree");

            // Enabling and disabling the same id is a plan error (CS2J9002)
            // rejected before any output directory is created.
            var (conflictExit, _, conflictErr) = await RunCliAsync(
                "preprocess", "--enable-pass", PassId, "--disable-pass", PassId,
                "-s", Path.Combine(dir, "Sample.csproj"),
                "-d", outConflict,
                "--no-verify-build", "--no-verify-tests");
            Assert.Equal(1, conflictExit);
            Assert.Contains("CS2J9002", conflictErr);
            Assert.False(Directory.Exists(outConflict));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SlowE2EFact(53.0)]
    public async Task DisablePass_OptsOutOfTheChannel()
    {
        var dir = CreateProject("Sample.csproj", """
            using System.Linq;
            public static class Tools
            {
                public static int[] Evens(int[] src)
                    => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
            }
            """);
        var outDir = Path.Combine(dir, "out");
        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--disable-pass", PassId,
                "-s", Path.Combine(dir, "Sample.csproj"),
                "-d", outDir,
                "--no-verify-build", "--no-verify-tests");

            Assert.True(exit == 0, stderr);
            Assert.False(
                Directory.Exists(Path.Combine(outDir, IntermediateDirName)),
                "--disable-pass linq-channel must keep the channel out of the chain");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- verify failure -------------------------------------------------------

    [Fact]
    public async Task VerifyBuildFailure_ExitsNonZero_AndReportsCs2j1351()
    {
        var dir = CreateProject("Broken.csproj", """
            public static class Broken
            {
                public static int F() => notDefined;
            }
            """);
        var outDir = Path.Combine(dir, "out");
        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", PassId,
                "-s", Path.Combine(dir, "Broken.csproj"),
                "-d", outDir,
                "--no-verify-tests");

            Assert.NotEqual(0, exit);
            Assert.Contains("CS2J1351", stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- strict LC0012 --------------------------------------------------------

    [Fact]
    public async Task StrictLc0012_FailsExit_NonStrict_Passes()
    {
        const string source = """
            using System;
            using System.Linq;
            using System.Linq.Expressions;

            public static class TreeConsumer
            {
                public static void Consume(Expression<Func<int, bool>> predicate) { }
            }

            public static class UserHelper
            {
                public static int Frob(int x) => x * 2;
            }

            public static class QuoteHost
            {
                public static void Use()
                    => TreeConsumer.Consume(x => UserHelper.Frob(x) > 1);

                // A real Enumerable chain in the same file so the R9
                // fast-skip does not bypass channel planning (which is what
                // surfaces the quoted LC0012 site above).
                public static int[] Other()
                    => new[] { 1, 2, 3 }.Where(x => x > 0).ToArray();
            }
            """;
        var dir = CreateProject("Quotes.csproj", source);

        var strictOut = Path.Combine(dir, "out-strict");
        var lenientOut = Path.Combine(dir, "out-lenient");
        try
        {
            // CLI strict DEFAULT is on (LocalFunctionStrict unless
            // --no-local-function-strict): LC0012 is an Error even though the
            // intermediate tree compiles.
            var (strictExit, _, strictErr) = await RunCliAsync(
                "preprocess", "--pass", PassId,
                "-s", Path.Combine(dir, "Quotes.csproj"),
                "-d", strictOut,
                "--no-verify-tests");
            Assert.NotEqual(0, strictExit);
            Assert.Contains("CS2J1303", strictErr);

            // Lenient: the same LC0012 is a Warning; build verification
            // succeeds and the pass exits 0.
            var (lenientExit, _, lenientErr) = await RunCliAsync(
                "preprocess", "--pass", PassId,
                "-s", Path.Combine(dir, "Quotes.csproj"),
                "-d", lenientOut,
                "--no-local-function-strict",
                "--no-verify-tests");
            Assert.True(lenientExit == 0, lenientErr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- LC0011 whole-file rollback (in-process chain) -----------------------

    [Fact]
    public async Task Lc0011_RollsFileBackToOriginal_FailsPass_ButKeepsTreeBuildable()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c26-m3-rollback-" + Guid.NewGuid().ToString("N"));
        var sourceDir = Path.Combine(tempRoot, "src");
        var outputDir = Path.Combine(tempRoot, "out");
        Directory.CreateDirectory(sourceDir);
        try
        {
            const string toolsSource = """
                using System.Linq;
                namespace RollbackProbe;
                public static class Tools
                {
                    public static int[] Evens(int[] src)
                        => src.Where(x => x % 2 == 0).Select(x => x * 2).ToArray();
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Tools.cs"), toolsSource);
            await File.WriteAllTextAsync(
                Path.Combine(sourceDir, "RollbackProbe.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n"
                + "    <TargetFramework>net10.0</TargetFramework>\n"
                + "    <ImplicitUsings>enable</ImplicitUsings>\n"
                + "  </PropertyGroup>\n</Project>\n");

            var originalBytes = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "Tools.cs"));
            var errorCapture = new StringWriter();
            var originalError = Console.Error;
            CliChainResult result;
            try
            {
                Console.SetError(errorCapture);
                LinqChannelPassAdapter.Lc0011FaultInjector = path =>
                    path.EndsWith("Tools.cs", StringComparison.OrdinalIgnoreCase)
                        ? new LinqRouteInvariantException(
                            "injected LC0011 tripwire (M3 test seam)", path, line: 1)
                        : null;

                var registry = PassCatalog.BuildRegistry();
                result = await CliPreprocessChain.RunAsync(new CliChainRequest
                {
                    SourcePath = Path.Combine(sourceDir, "RollbackProbe.csproj"),
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
                    LinqChannelOptionsFactory = () => new LinqChannelOptions(),
                    LegacyToggles = PassChainFactory.BuildAllDisabledToggles(registry),
                    EnablePassIds = new[] { PassId },
                    VerifyBuild = true,
                    VerifyTests = false,
                });
            }
            finally
            {
                LinqChannelPassAdapter.Lc0011FaultInjector = null;
                Console.SetError(originalError);
            }

            Assert.False(result.Success, "an LC0011 rollback must fail the pass");
            Assert.Contains(IntermediateDirName, result.ExecutedIntermediateDirectoryNames);
            Assert.Contains("CS2J1302", errorCapture.ToString());

            var rolledBack = Path.Combine(outputDir, IntermediateDirName, "Tools.cs");
            Assert.True(File.Exists(rolledBack));
            Assert.Equal(
                originalBytes,
                await File.ReadAllBytesAsync(rolledBack));
        }
        finally
        {
            LinqChannelPassAdapter.Lc0011FaultInjector = null;
            try { Directory.Delete(tempRoot, recursive: true); }
            catch (IOException) { }
        }
    }
}
