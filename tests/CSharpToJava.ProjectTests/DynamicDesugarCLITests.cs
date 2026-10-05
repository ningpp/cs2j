using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the C25 dynamic-desugar pass M1 Audit mode:
/// the pass is Implemented but default-off, writes dynamic-audit-report.json,
/// and copies every source byte-for-byte (zero rewrites until M2).
/// </summary>
public class DynamicDesugarCLITests
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
            "cs2j-dynamic-desugar-" + Guid.NewGuid().ToString("N"));
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
namespace DynamicSample;

public class Box
{
    public static object Answer()
    {
        dynamic d = 1;
        return d + 1;
    }
}
""";

    private static string ReportPath(string destDir)
        => Path.Combine(destDir, ".cs2j-dynamic-desugar-src", "dynamic-audit-report.json");

    [Fact]
    public async Task ListPasses_ShowsDynamicDesugarImplemented_DefaultOff()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        // Match the id at row start: java-keywords' run-after column also
        // references dynamic-desugar.
        var line = Assert.Single(
            stdout.Split('\n'),
            row => row.TrimStart().StartsWith("dynamic-desugar", StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        // Default column renders "off" padded to width; it must not render on.
        Assert.Contains("off", line);
        Assert.DoesNotContain(" on ", line);
        Assert.Contains("eliminate-goto", line);
        Assert.Contains("C25", line);
    }

    [Fact]
    public async Task AuditMode_WritesReportSummaryAndCopiesSourcesVerbatim()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "dynamic-desugar",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            // Audit is non-fatal in M1: site findings are info/warning only.
            Assert.DoesNotContain("error ", stderr, StringComparison.OrdinalIgnoreCase);

            // Only the dynamic-desugar intermediate tree exists.
            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-dynamic-desugar-src", intermediateDirs[0]);

            // Source copied verbatim (Audit mode never rewrites).
            var copied = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-dynamic-desugar-src", "Class1.cs"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(sourceDir, "Class1.cs")), copied);

            var reportPath = ReportPath(destDir);
            Assert.True(File.Exists(reportPath), $"report not found at {reportPath}");

            var bytes = await File.ReadAllBytesAsync(reportPath);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "report must be UTF-8 without BOM");

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            Assert.Equal("audit", root.GetProperty("mode").GetString());
            Assert.True(root.TryGetProperty("totals", out _), "report must carry totals");

            Assert.Contains("dynamic-desugar:", stdout);
            Assert.Contains("mode=Audit", stdout);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultOff_DoesNotCreateTheIntermediateTree()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            // A different pass selected explicitly: dynamic-desugar stays off.
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "conditional",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            Assert.Equal(string.Empty, stderr);
            Assert.False(Directory.Exists(
                Path.Combine(destDir, ".cs2j-dynamic-desugar-src")));
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
