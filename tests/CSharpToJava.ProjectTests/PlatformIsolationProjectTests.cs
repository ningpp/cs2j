using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C42 M2 project-level gate (design §7.2/§7.4). Runs the standalone
/// <c>preprocess --pass platform-isolation</c> entry against the fixtures with an
/// intermediate root under the (short) temp path to avoid the known deep-path
/// testhost Win32Exception 206.
/// </summary>
public class PlatformIsolationProjectTests
{
    private const string IntermediateDirName = ".cs2j-platform-isolation-src";

    private static string CliPath => TestCliPaths.LocateCli();

    private static string FixtureDir(string name)
        => Path.Combine(AppContext.BaseDirectory, name);

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

    private static string TempDir(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static string ReportPath(string destDir)
        => Path.Combine(destDir, IntermediateDirName, "platform-isolation-report.json");

    [Fact]
    public async Task Reject_Interop_FailsWithPI030_AndReportListsCS2J3203()
    {
        var destDir = TempDir("cs2j-pi-reject-");
        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Reject",
                "-s", FixtureDir("SamplePlatformIsolationInteropSolution"), "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("PI030", output);
            Assert.Contains("CS2J3203", output);

            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            var sites = document.RootElement.GetProperty("sites").EnumerateArray().ToList();
            // NativeMethods.cs is registered (no filename escape) — CS2J3203 present.
            Assert.Contains(sites, s => s.GetProperty("code").GetString() == "CS2J3203");

            // Semantic capture independent of the using form (global using + alias).
            var identities = sites
                .Select(s => s.GetProperty("identity").GetString())
                .Where(i => i != null)
                .ToList();
            Assert.Contains("System.IO.Ports.SerialPort", identities);
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Stub_VerifyBuildAndTestsGreen_WithGeneratedShim()
    {
        var destDir = TempDir("cs2j-pi-stub-");
        var configPath = Path.Combine(Path.GetTempPath(), "cs2j-pi-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(configPath, "{\"policies\":{\"PlatformType\":\"stub\"}}");

        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--platform-config", configPath,
                "-s", FixtureDir("SamplePlatformIsolationSolution"), "-d", destDir);

            Assert.True(exit == 0, output);
            Assert.Contains("mode=Stub", output);

            var intermediateRoot = Path.Combine(destDir, IntermediateDirName);
            Assert.True(File.Exists(Path.Combine(intermediateRoot, "generated", "Cs2jPlatformProbe.g.cs")));

            var osInfo = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "SamplePlatformIsolationLib", "OsInfo.cs"));
            Assert.Contains("Cs2j.Platform.Cs2jPlatformProbe.IsWindows()", osInfo);
            Assert.DoesNotContain("System.OperatingSystem.IsWindows()", osInfo);

            var wrapper = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "SamplePlatformIsolationLib", "PlatformWrappers.cs"));
            Assert.Contains("object port", wrapper);
            Assert.Contains("System.PlatformNotSupportedException", wrapper);

            var guarded = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "SamplePlatformIsolationLib", "GuardedApi.cs"));
            Assert.DoesNotContain("SupportedOSPlatform", guarded);

            // Reconciliation invariant on the real fixture.
            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            var totals = document.RootElement.GetProperty("totals");
            var sum = totals.GetProperty("reject").GetInt32()
                      + totals.GetProperty("stub").GetInt32()
                      + totals.GetProperty("facade").GetInt32()
                      + totals.GetProperty("suppressed").GetInt32()
                      + totals.GetProperty("residual").GetInt32();
            Assert.Equal(totals.GetProperty("sites").GetInt32(), sum);
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Stub_IsIdempotentAcrossRuns()
    {
        var firstDest = TempDir("cs2j-pi-idem-a-");
        var secondDest = TempDir("cs2j-pi-idem-b-");
        var configPath = Path.Combine(Path.GetTempPath(), "cs2j-pi-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(configPath, "{\"policies\":{\"PlatformType\":\"stub\"}}");
        try
        {
            var fixture = FixtureDir("SamplePlatformIsolationSolution");
            var first = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--platform-config", configPath,
                "--no-verify-build", "--no-verify-tests", "-s", fixture, "-d", firstDest);
            var second = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--platform-config", configPath,
                "--no-verify-build", "--no-verify-tests", "-s", fixture, "-d", secondDest);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);

            var firstOsInfo = await File.ReadAllTextAsync(
                Path.Combine(firstDest, IntermediateDirName, "SamplePlatformIsolationLib", "OsInfo.cs"));
            var secondOsInfo = await File.ReadAllTextAsync(
                Path.Combine(secondDest, IntermediateDirName, "SamplePlatformIsolationLib", "OsInfo.cs"));
            Assert.Equal(firstOsInfo, secondOsInfo);
        }
        finally
        {
            if (Directory.Exists(firstDest)) Directory.Delete(firstDest, recursive: true);
            if (Directory.Exists(secondDest)) Directory.Delete(secondDest, recursive: true);
            if (File.Exists(configPath)) File.Delete(configPath);
        }
    }

    [Fact]
    public async Task Stub_ProbeNameCollision_FailsFast()
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), "cs2j-pi-collide-" + Guid.NewGuid().ToString("N"));
        var destDir = Path.Combine(Path.GetTempPath(), "cs2j-pi-collide-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(sourceDir, "Class1.cs"), """
            namespace Sample;
            internal static class Cs2jPlatformProbe { }
            public static class Probe { public static bool A() => System.OperatingSystem.IsWindows(); }
            """);

        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("collides", output);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }
}