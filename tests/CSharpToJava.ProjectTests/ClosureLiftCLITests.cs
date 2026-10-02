using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for closure-lift (design §7.4): the isolated
/// <c>preprocess --pass closure-lift</c> verb runs the fixture through build
/// AND test verification, and --list-passes shows it Implemented/default-off.
/// </summary>
public sealed class ClosureLiftCLITests
{
    private const string IntermediateDir = ".cs2j-closure-lift-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleClosureLiftSolution");

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

            throw new FileNotFoundException("Could not locate CSharpToJava.CLI.dll.");
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
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task ListPasses_ShowsClosureLift_ImplementedAndOffByDefault()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("closure-lift"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" off ", line);
        Assert.DoesNotContain(" on ", line);
        Assert.Contains("C32", line);
    }

    [Fact]
    public async Task IsolatedPass_FixtureBuildsAndTestsVerify_ExitZero()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(),
            "cs2j-c32-cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "closure-lift",
                "-s", Path.Combine(FixtureDir, "SampleClosureLiftSolution.sln"),
                "-d", outputRoot);

            Assert.Equal(0, exit);
            Assert.DoesNotContain("CS2J", stderr);

            var intermediate = Path.Combine(outputRoot, IntermediateDir);
            var scenarios = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleClosureLiftLib", "CaptureScenarios.cs"));
            Assert.Contains("Cs2jDisplayClass", scenarios);

            // The verifier runs the fixture's RuntimeBehaviorTests on the
            // intermediate tree; accept the dotnet-test success marker in both
            // the invariant ("Passed!") and localized ("已通过!") wording.
            Assert.True(
                stdout.Contains("Passed!") || stdout.Contains("已通过"),
                "intermediate-tree test run marker missing from verb output");
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }
}
