using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyCLITests
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

    [Fact]
    public async Task Verb_SingleFile_RecordStruct_RewrittenToReadonly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record struct P(int X) { public int Y { get; set; } }");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "record-readonly", "-i", inputPath);

            Assert.Equal(0, exit);
            Assert.Contains("readonly record struct P", stdout);
            Assert.Contains("init", stdout);
            Assert.Contains("RR001", stderr);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_SingleFile_RecordClass_RequiresConvertRecordClassesFlag()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-rc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record class P { public int X { get; set; } }");

        try
        {
            // 不加 --convert-record-classes → record class 原样不动。
            var (exit1, stdout1, _) = await RunCliAsync("record-readonly", "-i", inputPath);
            Assert.Equal(0, exit1);
            Assert.Contains("public int X { get; set; }", stdout1);
            Assert.DoesNotContain("init", stdout1);

            // 加 --convert-record-classes → set→init。
            var (exit2, stdout2, stderr2) = await RunCliAsync(
                "record-readonly", "-i", inputPath, "--convert-record-classes");
            Assert.Equal(0, exit2);
            Assert.Contains("public int X { get; init; }", stdout2);
            Assert.Contains("RR002", stderr2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_Project_IntermediateTreeBuilds()
    {
        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "SampleRecordSolution");
        var outputDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-proj-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "record-readonly",
                "-s", fixtureDir,
                "-d", outputDir);

            Assert.Equal(0, exit);
            Assert.Contains("record-readonly:", stdout);
            Assert.Contains("struct(s)", stdout);
            Assert.Contains("Preprocessed source:", stdout);

            // 中间树目录存在且可编译。
            var intermediateDir = Path.Combine(outputDir, ".cs2j-record-readonly-src");
            Assert.True(Directory.Exists(intermediateDir));
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_Strict_NonZeroExitWhenSkipped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-strict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public record struct P { public event System.Action? Ev; public int X { get; set; } }");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "record-readonly", "-i", inputPath, "--strict");

            Assert.NotEqual(0, exit);
            Assert.Contains("RR040", stderr);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
