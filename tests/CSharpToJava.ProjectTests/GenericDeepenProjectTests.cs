using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end coverage for the generic-deepen (C02) preprocess pass on the
/// SampleGenericDeepenSolution fixture: full-chain CLI run with verify build+tests,
/// holder generation, and intermediate-tree shape.
/// </summary>
public class GenericDeepenProjectTests
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
    public async Task Solution_EnableGenericDeepen_RewritesBuildsAndTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleGenericDeepenSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleGenericDeepenSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleGenericDeepenSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-gd-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--enable-pass", "generic-deepen");

            // VerifyBuild/VerifyTestsByDefault: the runner already executed dotnet build +
            // dotnet test on the intermediate tree (RuntimeBehaviorTests = the semantics
            // proof); any failure exits non-zero here.
            Assert.True(exit == 0,
                $"preprocess --enable-pass generic-deepen failed (exit {exit}).\nstderr: {stderr}\nstdout tail: {stdout[^Math.Min(2000, stdout.Length)..]}");

            var deepenRoot = Path.Combine(destination, ".cs2j-generic-deepen-src");
            Assert.True(Directory.Exists(deepenRoot), "generic-deepen intermediate tree must exist");

            var holderFiles = Directory.GetFiles(deepenRoot, "_Cs2jGenericHolders.*.cs");
            Assert.NotEmpty(holderFiles);

            var genericSamples = Directory.GetFiles(deepenRoot, "GenericSamples.cs", SearchOption.AllDirectories).Single();
            var genericSamplesText = await File.ReadAllTextAsync(genericSamples);
            Assert.Contains("__cs2j_token_", genericSamplesText);
            Assert.Contains("catch (global::System.Exception __cs2j_e0)", genericSamplesText);
            Assert.Contains("typeof(TException).IsAssignableFrom(__cs2j_e0.GetType())", genericSamplesText);

            var staticSamplesText = await File.ReadAllTextAsync(
                Directory.GetFiles(deepenRoot, "StaticSamples.cs", SearchOption.AllDirectories).Single());
            Assert.Contains("__Cs2jGenericHolder_Latest", staticSamplesText);
            Assert.DoesNotContain("public static T? Value;", staticSamplesText);
            Assert.Contains("public static T Identity<T>(T value)", staticSamplesText);

            var libCsprojText = await File.ReadAllTextAsync(
                Directory.GetFiles(deepenRoot, "SampleGenericDeepenLib.csproj", SearchOption.AllDirectories).Single());
            Assert.Contains("_Cs2jGenericHolders.SampleGenericDeepenLib.cs", libCsprojText);

            var appProgramText = await File.ReadAllTextAsync(
                Directory.GetFiles(deepenRoot, "Program.cs", SearchOption.AllDirectories).Single());
            Assert.Contains("__cs2j_get_Empty<string>(typeof(string))", appProgramText);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
