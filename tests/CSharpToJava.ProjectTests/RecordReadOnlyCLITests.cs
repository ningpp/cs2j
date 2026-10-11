using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

public class RecordReadOnlyCLITests
{
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
        // 私有副本驱动 CLI：RecordReadOnlyProjectTests 与本测试类会被 xunit 并行执行，
        // 直接以 bin 下的共享 fixture 为源会与它们的 .test-output-* 创建/删除竞态。
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-rr-cli-proj-" + Guid.NewGuid().ToString("N"));
        var fixtureDir = Path.Combine(tempDir, "fixture");
        CopyFixture(AppContext.BaseDirectory, "SampleRecordSolution", fixtureDir);
        var outputDir = Path.Combine(tempDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "record-readonly",
                "-s", fixtureDir,
                "-d", outputDir);

            Assert.True(exit == 0,
                $"exit={exit}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
            Assert.Contains("record-readonly:", stdout);
            Assert.Contains("struct(s)", stdout);
            Assert.Contains("Preprocessed source:", stdout);

            // 中间树目录存在且可编译。
            var intermediateDir = Path.Combine(outputDir, ".cs2j-record-readonly-src");
            Assert.True(Directory.Exists(intermediateDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void CopyFixture(string baseDirectory, string name, string destination)
    {
        var sourceRoot = Path.Combine(baseDirectory, name);
        foreach (var file in Directory.EnumerateFiles(
            sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 排除构建产物与其它测试的输出目录（正常情况下 bin 下的 fixture 没有这些）。
            if (segments.Any(s => s is "bin" or "obj" || s.StartsWith(".test-output", StringComparison.Ordinal)))
                continue;

            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
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
