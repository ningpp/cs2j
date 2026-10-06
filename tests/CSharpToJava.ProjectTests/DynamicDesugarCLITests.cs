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

    // 全默认链探针：不能含 dynamic——numeric-boundary 既有 pass 对
    // 「动态绑定 decimal 运算」发 CS2J5362 Error，与 C25 audit 无关。
    // 链在否只看 .cs2j-dynamic-desugar-src 与报告是否生成。
    private const string CleanChainProbeSource = """
namespace DynamicSample;

public class Box
{
    public static int Answer(int a, int b) => a + b;
}
""";

    private static string ReportPath(string destDir)
        => Path.Combine(destDir, ".cs2j-dynamic-desugar-src", "dynamic-audit-report.json");

    [Fact]
    public async Task ListPasses_ShowsDynamicDesugarImplemented_AuditDefaultOn()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        // Match the id at row start: java-keywords' run-after column also
        // references dynamic-desugar.
        var line = Assert.Single(
            stdout.Split('\n'),
            row => row.TrimStart().StartsWith("dynamic-desugar", StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        // Default column renders "on" padded to width; it must not render off.
        Assert.Contains(" on ", line);
        Assert.DoesNotContain(" off ", line);
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
    public async Task ExplicitOtherPassSelection_DoesNotCreateTheDynamicTree()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", ProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            // A different pass selected explicitly: dynamic-desugar is not in
            // that one-pass plan even though audit is default-on in the chain.
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

    [Fact]
    public async Task DefaultChain_RunsAudit_AndWritesReport()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", CleanChainProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"default chain failed:\n{stderr}\n{stdout}");
            Assert.True(Directory.Exists(
                Path.Combine(destDir, ".cs2j-dynamic-desugar-src")),
                "dynamic-desugar audit flipped default-on (2026-10-06) and must run in the default chain");
            Assert.True(File.Exists(ReportPath(destDir)), "audit report must be written");
            Assert.Contains("mode=Audit", stdout);

            // Audit never rewrites sources.
            var copied = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-dynamic-desugar-src", "Class1.cs"));
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(sourceDir, "Class1.cs")), copied);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisablePass_OptsOutOfAudit()
    {
        // 独立工作区：与默认链用例分开，避免上一轮预处理支撑文件残留导致
        // extension-methods 支撑类重复定义（CS0101/CS0111）。
        var sourceDir = CreateTempWorkspace("Lib.csproj", CleanChainProbeSource);
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--disable-pass", "dynamic-desugar",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            Assert.True(exit == 0, $"--disable-pass run failed:\n{stderr}");
            Assert.False(Directory.Exists(
                Path.Combine(destDir, ".cs2j-dynamic-desugar-src")),
                "--disable-pass dynamic-desugar must keep audit out of the chain");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
