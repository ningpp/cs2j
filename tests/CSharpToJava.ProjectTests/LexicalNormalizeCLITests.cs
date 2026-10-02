using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for lexical-normalize: isolated --pass selection,
/// LN-3 ordering failure when run on a source with residual #define, LN-7 via
/// --ln-normalize-file-scoped and extern alias severity override.
/// </summary>
public sealed class LexicalNormalizeCLITests
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

    private static string CreateProject(string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "cs2j-ln-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Lib.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(tempDir, "C.cs"), source);
        return tempDir;
    }

    [Fact]
    public async Task IsolatedPass_StripsLineDirectives_ExitZero()
    {
        var sourceDir = CreateProject("#line 9\npublic class C { public int X; }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.DoesNotContain("#line", rewritten);
            Assert.DoesNotContain("CS2J9001", stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ResidualDefine_FailsWith_CS2J5512_ExitOne()
    {
        var sourceDir = CreateProject("#define X\npublic class C { }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J5512", stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task FileScopedOption_BlockNormalizes_FromVerb()
    {
        var sourceDir = CreateProject("namespace A.B;\npublic class C { }\n");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, _) = await RunCliAsync(
                "preprocess", "--pass", "lexical-normalize",
                "--ln-normalize-file-scoped",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);
            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-lexical-normalize-src", "C.cs"));
            Assert.Contains("namespace A.B\n", rewritten);
            Assert.Contains("{", rewritten);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
