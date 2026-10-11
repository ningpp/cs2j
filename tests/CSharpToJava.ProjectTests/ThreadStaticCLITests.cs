using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the thread-static pass (C09, design §7.5):
/// --list-passes shows it Implemented and default-on (flipped 2026-10-06);
/// the default chain now creates its intermediate tree; --pass runs it alone
/// and rewrites; --enable-pass chains it after generic; --disable-pass keeps
/// it off; the conversion fingerprint carries the pass:thread-static token.
/// These tests execute the Debug CLI dll.
/// </summary>
[Collection("ThreadStaticSerial")]
public class ThreadStaticCLITests
{
    private const string IntermediateDir = ".cs2j-thread-static-src";

    private static string CliPath => TestCliPaths.LocateCli();

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
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private const string ProjectName = "TsCliLib";
    private const string SolutionName = "TsCli.sln";

    /// <summary>
    /// Realistic solution layout (sln at the root, project in a subdirectory),
    /// matching the SampleThreadStaticSolution fixture and production converts:
    /// later chain passes' generated helper files land beside the .sln, outside
    /// the project's default compile glob.
    /// </summary>
    private static string CreateTempWorkspace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-thread-static-cli-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(tempDir, ProjectName);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, ProjectName + ".csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(projectDir, "Class1.cs"), """
            using System;

            namespace TsCli;

            public static class Counter
            {
                [ThreadStatic] private static int _hits;

                public static int Bump() => ++_hits;

                public static int Current => _hits;
            }
            """);
        File.WriteAllText(Path.Combine(tempDir, SolutionName), """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "TsCliLib", "TsCliLib\TsCliLib.csproj", "{410900A0-0000-4000-8000-000000000001}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{410900A0-0000-4000-8000-000000000001}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{410900A0-0000-4000-8000-000000000001}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{410900A0-0000-4000-8000-000000000001}.Release|Any CPU.ActiveCfg = Release|Any CPU
            		{410900A0-0000-4000-8000-000000000001}.Release|Any CPU.Build.0 = Release|Any CPU
            	EndGlobalSection
            	GlobalSection(SolutionProperties) = preSolution
            		HideSolutionNode = FALSE
            	EndGlobalSection
            EndGlobal
            """);
        return tempDir;
    }

    private static string[] IntermediateDirs(string destDir)
        => Directory.GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .ToArray()!;

    [Fact]
    public async Task ListPasses_ShowsThreadStatic_ImplementedAndDefaultOn()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("thread-static"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.DoesNotContain("Stub", line);
    }

    private static string RewrittenClassFile(string destDir)
        => Path.Combine(destDir, IntermediateDir, ProjectName, "Class1.cs");

    [Fact]
    public async Task PassSelection_RunsOnlyThreadStatic_AndRewritesToThreadLocalHolder()
    {
        var sourceDir = CreateTempWorkspace();
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "thread-static",
                "--no-verify-build", "--no-verify-tests",
                "-s", Path.Combine(sourceDir, SolutionName), "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --pass thread-static failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");
            Assert.Equal(string.Empty, stderr);

            var dirs = IntermediateDirs(destDir);
            Assert.Single(dirs);
            Assert.Equal(IntermediateDir, dirs[0]);

            var rewritten = await File.ReadAllTextAsync(RewrittenClassFile(destDir));
            Assert.Contains("global::System.Threading.ThreadLocal<int> _hits", rewritten);
            Assert.Contains("new global::System.Threading.ThreadLocal<int>()", rewritten);
            Assert.Contains("=> ++_hits.Value", rewritten);
            Assert.DoesNotContain("[ThreadStatic]", rewritten);
            Assert.Contains("thread-static:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [SlowE2EFact(87.6)]
    public async Task DefaultChain_ProducesThreadStaticTree_AndDisablePassOptsOut()
    {
        var sourceDir = CreateTempWorkspace();
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess",
                "--no-verify-build", "--no-verify-tests",
                "-s", Path.Combine(sourceDir, SolutionName), "-d", destDir);

            Assert.True(exit == 0,
                $"default preprocess chain failed (exit {exit}).\nstderr: {stderr}");
            Assert.True(Directory.Exists(Path.Combine(destDir, IntermediateDir)),
                "thread-static is default-on: the default chain must create its intermediate tree");
            var rewritten = await File.ReadAllTextAsync(RewrittenClassFile(destDir));
            Assert.Contains("global::System.Threading.ThreadLocal<int> _hits", rewritten);
            Assert.Contains("thread-static:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }

        // --disable-pass is the supported opt-out. A fresh workspace is used:
        // support files emitted by earlier passes are workspace-local.
        var optOutWorkspace = CreateTempWorkspace();
        try
        {
            var optOutDir = Path.Combine(optOutWorkspace, "out-off");
            var (offExit, _, offStderr) = await RunCliAsync(
                "preprocess",
                "--disable-pass", "thread-static",
                "--no-verify-build", "--no-verify-tests",
                "-s", Path.Combine(optOutWorkspace, SolutionName), "-d", optOutDir);
            Assert.True(offExit == 0,
                $"disabled chain failed (exit {offExit}).\nstderr: {offStderr}");
            Assert.False(Directory.Exists(Path.Combine(optOutDir, IntermediateDir)),
                "--disable-pass thread-static must suppress the intermediate tree");
        }
        finally
        {
            Directory.Delete(optOutWorkspace, recursive: true);
        }
    }

    [SlowE2EFact(45.9)]
    public async Task EnablePass_ChainsAfterGeneric_ProducesRewrittenThreadStaticTree()
    {
        var sourceDir = CreateTempWorkspace();
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--enable-pass", "thread-static",
                "--no-verify-build", "--no-verify-tests",
                "-s", Path.Combine(sourceDir, SolutionName), "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --enable-pass thread-static failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");

            // The pass joined the DEFAULT chain at its registered slot — generic
            // (upstream) and thread-static intermediate trees both exist, and the
            // thread-static output is the rewritten ThreadLocal holder.
            Assert.True(Directory.Exists(Path.Combine(destDir, ".cs2j-generic-src")),
                "the default generic pass must still chain before thread-static");
            var threadStaticTree = Path.Combine(destDir, IntermediateDir);
            Assert.True(Directory.Exists(threadStaticTree),
                "an enabled thread-static pass must create its intermediate tree");

            var rewritten = await File.ReadAllTextAsync(RewrittenClassFile(destDir));
            Assert.Contains("ThreadLocal<int>", rewritten);
            Assert.Contains("_hits.Value", rewritten);
            Assert.Contains("thread-static:", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [SlowE2EFact(43.4)]
    public async Task DisablePass_KeepsThreadStaticTreeAbsent()
    {
        var sourceDir = CreateTempWorkspace();
        var destDir = Path.Combine(sourceDir, "out-off");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--disable-pass", "thread-static",
                "--no-verify-build", "--no-verify-tests",
                "-s", Path.Combine(sourceDir, SolutionName), "-d", destDir);

            Assert.True(exit == 0,
                $"preprocess --disable-pass thread-static failed (exit {exit}).\nstderr: {stderr}");
            Assert.False(Directory.Exists(Path.Combine(destDir, IntermediateDir)),
                "the kill switch must keep thread-static off even when explicitly passed");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public void ConversionFingerprint_IncludesThreadStaticPassToken_DefaultOn()
    {
        var tokens = CSharpToJava.CLI.Program.GetProjectConversionOptionTokens(
            new CSharpToJava.CLI.ConvertProjectOptions());

        // Every registered pass contributes one token in registry order; the
        // default-on C09 token must be present and pinned to True.
        Assert.Contains("pass:thread-static=True", tokens);
        Assert.Single(tokens, t => t.StartsWith("pass:thread-static=", StringComparison.Ordinal));
    }
}
