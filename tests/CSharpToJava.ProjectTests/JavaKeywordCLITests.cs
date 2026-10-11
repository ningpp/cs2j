using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

public class JavaKeywordCLITests
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
    public async Task Verb_SingleFile_Method_True_Renamed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-jk-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        await File.WriteAllTextAsync(inputPath,
            "public class C { public void True() { } public void M() => True(); }");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "java-keywords", "-i", inputPath);

            Assert.Equal(0, exit);
            Assert.Contains("TrueValue()", stdout);
            Assert.Contains("void TrueValue()", stdout);
            Assert.DoesNotContain("void True()", stdout);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Verb_SingleFile_NoKeywords_Unchanged()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-jk-cli-clean-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "P.cs");
        var src = "public class C { public void Foo() { } }";
        await File.WriteAllTextAsync(inputPath, src);

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "java-keywords", "-i", inputPath);

            Assert.Equal(0, exit);
            Assert.Contains("void Foo()", stdout);
            Assert.DoesNotContain("Value", stdout);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
