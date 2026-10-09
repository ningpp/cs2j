using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the C42 platform-isolation pass: report JSON
/// emission, summary line, strict behavior, default-off zero change, the
/// Reject/Stub apply modes and the M3 Facade mode (port + seam + slot).
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
    public async Task FacadeMode_GeneratesPortSeamAndSlot_AndRestoredTreeBuilds()
    {
        const string facadeSource = """
            namespace PlatformSample;

            public class RegistryWrapper
            {
                public void Work(Microsoft.Win32.RegistryKey key)
                {
                    System.Console.WriteLine(key);
                }
            }
            """;
        var sourceDir = CreateTempWorkspace("Lib.csproj", facadeSource);
        var destDir = Path.Combine(sourceDir, "out");
        var configPath = Path.Combine(sourceDir, "Cs2jPlatformIsolation.json");
        await File.WriteAllTextAsync(configPath, "{\"policies\":{\"PlatformType\":\"facade\"}}");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Facade",
                "--platform-config", configPath, "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            var output = stdout + stderr;
            Assert.True(exit == 0, output);

            var intermediateRoot = Path.Combine(destDir, ".cs2j-platform-isolation-src");
            var rewritten = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "Class1.cs"));
            Assert.Contains("partial class RegistryWrapper", rewritten);
            Assert.Contains("Cs2jRequiredPort.Work(key)", rewritten);
            Assert.DoesNotContain("Microsoft.Win32", rewritten);

            // 端口接口 + 用户槽位按 generated/<ns>/ 相对子路径落入项目目录，
            // 随中间树一起参与 restore-build（exit 0 即证明缝/端口/槽位可编译）。
            Assert.True(File.Exists(Path.Combine(
                intermediateRoot, "generated", "PlatformSample", "IRegistryWrapperPort.g.cs")));
            Assert.True(File.Exists(Path.Combine(
                intermediateRoot, "generated", "PlatformSample", "RegistryWrapper.PortSlot.g.cs")));

            Assert.Contains("mode=Facade", output);
            Assert.Contains("facade=1", output);
            Assert.Contains("PI020", output);

            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            var site = Assert.Single(document.RootElement.GetProperty("sites").EnumerateArray());
            Assert.Equal("facade", site.GetProperty("action").GetString());
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task RejectMode_FailsWithPI030_AndCopiesSourcesVerbatim()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Reject",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            var output = stdout + stderr;
            Assert.Contains("PI030", output);
            Assert.Contains("CS2J3102", output);

            // Sources are never rewritten in Reject mode; the intermediate dir is preserved.
            var copied = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-platform-isolation-src", "Class1.cs"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(sourceDir, "Class1.cs")), copied);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task StubMode_RedirectsProbeAndDistributesGeneratedShim()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "-s", sourceDir, "-d", destDir);

            var output = stdout + stderr;
            Assert.True(exit == 0, output);

            var intermediateRoot = Path.Combine(destDir, ".cs2j-platform-isolation-src");
            var rewritten = await File.ReadAllTextAsync(Path.Combine(intermediateRoot, "Class1.cs"));
            Assert.Contains("Cs2j.Platform.Cs2jPlatformProbe.IsWindows()", rewritten);
            Assert.DoesNotContain("System.OperatingSystem.IsWindows()", rewritten);

            var shim = Path.Combine(intermediateRoot, "generated", "Cs2jPlatformProbe.g.cs");
            Assert.True(File.Exists(shim), $"shim not found at {shim}");

            Assert.Contains("mode=Stub", output);

            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            var site = Assert.Single(document.RootElement.GetProperty("sites").EnumerateArray(),
                s => s.GetProperty("identity").GetString() == "System.OperatingSystem.IsWindows");
            Assert.Equal("probeRedirect", site.GetProperty("action").GetString());
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task StubMode_WithNoVerify_CreatesNoBuildOutput()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            var output = stdout + stderr;
            Assert.True(exit == 0, output);
            Assert.False(Directory.Exists(
                Path.Combine(destDir, ".cs2j-platform-isolation-src", "bin")));
            Assert.True(File.Exists(
                Path.Combine(destDir, ".cs2j-platform-isolation-src", "generated", "Cs2jPlatformProbe.g.cs")));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task StubMode_IsIdempotentAcrossRuns()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var firstDest = Path.Combine(Path.GetTempPath(), "cs2j-pi-stub-a-" + Guid.NewGuid().ToString("N"));
        var secondDest = Path.Combine(Path.GetTempPath(), "cs2j-pi-stub-b-" + Guid.NewGuid().ToString("N"));

        try
        {
            var first = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", firstDest);
            var second = await RunCliAsync(
                "preprocess", "--pass", "platform-isolation", "--platform-mode", "Stub",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", secondDest);

            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);

            var firstCode = await File.ReadAllTextAsync(
                Path.Combine(firstDest, ".cs2j-platform-isolation-src", "Class1.cs"));
            var secondCode = await File.ReadAllTextAsync(
                Path.Combine(secondDest, ".cs2j-platform-isolation-src", "Class1.cs"));
            Assert.Equal(firstCode, secondCode);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(firstDest)) Directory.Delete(firstDest, recursive: true);
            if (Directory.Exists(secondDest)) Directory.Delete(secondDest, recursive: true);
        }
    }

    [Fact]
    public async Task ListPasses_ShowsPlatformIsolationImplemented_DefaultOn()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        var line = stdout.Split('\n').First(l => l.Contains("platform-isolation"));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.Contains("C42", line);
    }

    private static string ReadSitesJson(string reportPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(reportPath));
        return document.RootElement.GetProperty("sites").GetRawText();
    }
}