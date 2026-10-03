using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the C42 platform-isolation pass M1 Report mode:
/// report JSON emission, summary line, strict behavior, default-off zero change
/// and the loud failure of unimplemented modes.
/// </summary>
public class PlatformIsolationCLITests
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
        process.WaitForExit();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string CreateTempWorkspace(string csprojName, string csharpSource)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-platform-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, csprojName),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(tempDir, "Class1.cs"), csharpSource);
        return tempDir;
    }

    private const string ProbeSource = """
namespace PlatformSample;

public static class OsInfo
{
    public static bool RunsOnWindows() => System.OperatingSystem.IsWindows();
}
""";

    private static string ReportPath(string destDir)
        => Path.Combine(destDir, ".cs2j-platform-isolation-src", "platform-isolation-report.json");

    [Fact]
    public async Task ReportMode_WritesReportSummaryAndCopiesSourcesVerbatim()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            // Per-site PI001 lines are Warnings by design (design §5), so stderr
            // carries them; only errors would fail the run.
            Assert.DoesNotContain("error ", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PI001", stderr);

            // Only the platform-isolation intermediate tree exists.
            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-platform-isolation-src", intermediateDirs[0]);

            // Source copied verbatim (Report mode never rewrites).
            var copied = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-platform-isolation-src", "Class1.cs"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(sourceDir, "Class1.cs")), copied);

            var reportPath = ReportPath(destDir);
            Assert.True(File.Exists(reportPath), $"report not found at {reportPath}");

            var bytes = await File.ReadAllBytesAsync(reportPath);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "report must be UTF-8 without BOM");

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            Assert.Equal("Report", root.GetProperty("mode").GetString());

            var site = Assert.Single(root.GetProperty("sites").EnumerateArray(),
                s => s.GetProperty("identity").GetString() == "System.OperatingSystem.IsWindows");
            Assert.Equal("platformMember", site.GetProperty("family").GetString());
            Assert.Equal("CS2J3102", site.GetProperty("code").GetString());
            Assert.Equal("stub", site.GetProperty("defaultAction").GetString());
            Assert.Equal("probeRedirect", site.GetProperty("action").GetString());
            Assert.Equal("PI001", site.GetProperty("diagnostic").GetString());

            Assert.Contains("platform-isolation:", stdout);
            Assert.Contains("probes", stdout);
            Assert.Contains("mode=Report", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ReportMode_ProducesByteStableSitesAcrossRuns()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);

        // Destinations live OUTSIDE the source tree so a second run never treats
        // the first run's intermediate tree as source.
        var firstDest = Path.Combine(Path.GetTempPath(), "cs2j-pi-out-a-" + Guid.NewGuid().ToString("N"));
        var secondDest = Path.Combine(Path.GetTempPath(), "cs2j-pi-out-b-" + Guid.NewGuid().ToString("N"));

        try
        {
            var first = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "-s", sourceDir, "-d", firstDest);
            var second = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "-s", sourceDir, "-d", secondDest);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);

            Assert.Equal(
                ReadSitesJson(ReportPath(firstDest)),
                ReadSitesJson(ReportPath(secondDest)));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(firstDest)) Directory.Delete(firstDest, recursive: true);
            if (Directory.Exists(secondDest)) Directory.Delete(secondDest, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultOff_DoesNotCreateTheIntermediateTree()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            // A different pass selected explicitly: platform-isolation stays off.
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "conditional",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.False(Directory.Exists(
                Path.Combine(destDir, ".cs2j-platform-isolation-src")));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task StrictMode_RaisesErrorSiteAndFailsTheRun()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-strict",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("platform-isolation", output);
            Assert.Contains("mode=Report", output);
            // Report is still written before the strict failure surfaces.
            Assert.True(File.Exists(ReportPath(destDir)));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task NonReportMode_FailsLoudlyWithM2Message()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("Stub", output);
            Assert.Contains("not implemented", output);
            Assert.False(File.Exists(ReportPath(destDir)),
                "an unimplemented mode must not silently degrade to Report");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    private static string ReadSitesJson(string reportPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        return document.RootElement.GetProperty("sites").GetRawText();
    }
}