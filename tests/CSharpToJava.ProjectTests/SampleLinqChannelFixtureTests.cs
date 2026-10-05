using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C26 M1 fixture gate for SampleLinqChannelSolution (SampleLinqLib +
/// SampleLinqApp + SampleLinqTests): the skeleton itself must build and its
/// xUnit truth tests must pass on the unprocessed source, establishing the
/// .NET half of the channel equivalence contract.
///
/// TODO (design §7.6, M2/M3): add the channel-side assertions — run the
/// linq-channel pass over the fixture through CliPreprocessChain, verify the
/// intermediate tree (1) builds, (2) passes the same xUnit truth tests,
/// (3) preserves the IQueryable repository chains, and (4) emits the
/// schemaVersion 2 report with the expected Enumerable/Queryable counters.
/// M1 wires the fixture registration and baseline only.
/// </summary>
public sealed class SampleLinqChannelFixtureTests
{
    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleLinqChannelSolution");

    private static string FixtureSlnPath =>
        Path.Combine(FixtureDir, "SampleLinqChannelSolution.sln");

    [Fact]
    public async Task Baseline_OriginalFixture_Builds_AndAllTruthTestsPass()
    {
        var resultsDirectory = Path.Combine(FixtureDir, ".test-output-baseline-trx");
        try
        {
            // Retry once for transient parallel-suite child failures.
            var exitCode = -1;
            var succeededAttempt = -1;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                exitCode = await RunFixtureTestsAsync(
                    FixtureSlnPath, resultsDirectory, $"baseline-{attempt}.trx");
                if (exitCode == 0)
                {
                    succeededAttempt = attempt;
                    break;
                }
            }

            Assert.Equal(0, exitCode);

            var trxPath = Path.Combine(resultsDirectory, $"baseline-{succeededAttempt}.trx");
            Assert.True(File.Exists(trxPath), "missing baseline trx from the successful run");
            var content = await File.ReadAllTextAsync(trxPath);
            Assert.Contains("passed=\"4\"", content);
            Assert.Contains("failed=\"0\"", content);
        }
        finally
        {
            if (Directory.Exists(resultsDirectory))
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
        }
    }

    private static async Task<int> RunFixtureTestsAsync(
        string slnPath, string resultsDirectory, string trxFileName)
    {
        Directory.CreateDirectory(resultsDirectory);
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{slnPath}\" --nologo "
                + $"--logger \"trx;LogFileName={trxFileName}\" "
                + $"--results-directory \"{resultsDirectory}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(10));
        _ = await stdoutTask + await stderrTask;

        return process.ExitCode;
    }
}
