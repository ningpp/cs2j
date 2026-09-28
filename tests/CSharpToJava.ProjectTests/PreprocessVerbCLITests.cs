using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the generic <c>preprocess</c> verb: catalog
/// listing, CS2J9002 rejection of unknown pass ids, and explicit single-pass
/// selection isolated from the default chain.
/// </summary>
public class PreprocessVerbCLITests
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
            "cs2j-preprocess-verb-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task ListPasses_PrintsAllRegisteredIds_AndExitsZero()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");

        Assert.Equal(0, exit);
        Assert.Contains("cs2j preprocess passes", stdout);
        Assert.Contains("conditional", stdout);
        Assert.Contains("eliminate-goto", stdout);
        Assert.Contains("java-keywords", stdout);
        Assert.Contains("off", stdout);
        Assert.Contains("on", stdout);
    }

    [Fact]
    public async Task UnknownPassId_FailsWith_CS2J9002_AndExitsOne()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj", "public class C { }");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "--pass", "no-such-pass",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(1, exit);
            Assert.Contains("CS2J9002", stderr);
            Assert.Contains("no-such-pass", stderr);
            Assert.False(Directory.Exists(destDir),
                "failure must be reported before any output directory is created");
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitPassSelection_RunsOnlyTheRequestedPass()
    {
        var sourceDir = CreateTempWorkspace("Lib.csproj",
            "public class C { public uint Counter = 42u; public ushort Other = 7; }");
        var destDir = Path.Combine(sourceDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "--pass", "unsigned",
                "-s", sourceDir, "-d", destDir);

            Assert.Equal(0, exit);

            // Only the unsigned intermediate tree must exist (no default-chain neighbours).
            var intermediateDirs = Directory
                .GetDirectories(destDir, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-unsigned-src", intermediateDirs[0]);

            var rewritten = await File.ReadAllTextAsync(
                Path.Combine(destDir, ".cs2j-unsigned-src", "Class1.cs"));
            Assert.Contains("System.UInt32", rewritten);
            Assert.Contains("System.UInt16", rewritten);

            // The unsigned summary ran; default-chain passes did not.
            Assert.Contains("unsigned-type:", stdout);
            Assert.DoesNotContain("make-readonly:", stdout);
            Assert.DoesNotContain("strip-conditional:", stdout);
            Assert.Equal(string.Empty, stderr);
        }
        finally
        {
            Directory.Delete(sourceDir, recursive: true);
        }
    }
}
