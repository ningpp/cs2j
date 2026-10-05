using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end coverage for C49 M2 extension members on the
/// SampleExtensionMembersSolution fixture:
///   * the raw solution builds and its runtime behavior tests pass (extension
///     blocks are valid C# 14 preview source);
///   * under the default preprocess policy (--materialize-extension-blocks
///     stays off), extension blocks still hit the classic CS2J1206 syntax
///     gate and nothing is half-materialized;
///   * the indexer negative sample ships with the fixture (indexer rejection
///     CS2J1209 itself is asserted at the analyzer level in the unit tests).
/// </summary>
public class ExtensionMembersProjectTests
{
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
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static (int ExitCode, string Output) RunDotnet(string arguments, string workDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    [Fact]
    public void Fixture_BuildsAndRuntimeTestsPass()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtensionMembersSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtensionMembersSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleExtensionMembersSolution.sln");

        var (buildExit, buildOutput) = RunDotnet(
            $"build \"{solutionPath}\" --nologo -v q", fixture);
        Assert.True(buildExit == 0,
            $"fixture build failed:\n{buildOutput[^Math.Min(2000, buildOutput.Length)..]}");

        var (testExit, testOutput) = RunDotnet(
            $"test \"{solutionPath}\" --nologo -v q --no-build", fixture);
        Assert.True(testExit == 0,
            $"fixture runtime tests failed:\n{testOutput[^Math.Min(2000, testOutput.Length)..]}");
    }

    [Fact]
    public async Task Preprocess_DefaultPolicy_BlocksHitClassicGate1206()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtensionMembersSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtensionMembersSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleExtensionMembersSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            // --materialize-extension-blocks is not passed; the
            // extension-methods pass must keep its classic CS2J1206 gate
            // for extension blocks.
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "extension-methods,member-desugar");

            Assert.NotEqual(0, exit);
            var combined = stdout + "\n" + stderr;
            Assert.Contains("CS2J1206", combined);
            // The block declarations must not be partially materialized: no
            // synthesized 'this global::' receiver parameters anywhere.
            var materialized = Directory
                .EnumerateFiles(destination, "*.cs", SearchOption.AllDirectories)
                .Any(f => File.ReadAllText(f).Contains("this global::", StringComparison.Ordinal));
            Assert.False(materialized, "extension blocks must not be materialized under the default policy");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Fixture_NegativeSample_IndexerShapeShips()
    {
        var negativeSample = Path.Combine(
            AppContext.BaseDirectory, "SampleExtensionMembersSolution",
            "SampleExtensionMembersLib", "Negatives", "IndexerBlock.cs");
        Assert.True(File.Exists(negativeSample),
            "the indexer negative sample must ship with the fixture");
        var text = File.ReadAllText(negativeSample);
        Assert.Contains("this[int index]", text);
    }
}
