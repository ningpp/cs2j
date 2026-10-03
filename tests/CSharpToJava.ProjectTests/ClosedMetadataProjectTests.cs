using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end coverage for the closed-metadata (C30) preprocess pass on the
/// SampleClosedMetadataSolution fixture: full-chain CLI run (generic-deepen +
/// closed-metadata, design §4.4 hard chain dependency) with verify build+tests,
/// registration-file generation/ownership, and csproj wiring.
/// </summary>
public class ClosedMetadataProjectTests
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
    public async Task Solution_EnableClosedMetadata_GeneratesRegistrationsBuildsAndTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleClosedMetadataSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleClosedMetadataSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleClosedMetadataSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-cm-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            // --pass selects EXACTLY these ids: the hard chain dependency (design §4.4)
            // requires generic-deepen and closed-metadata in the same run (CS2J1205
            // otherwise), and the closed-metadata registration reads the post-C02
            // symbol shapes of the generic-deepen output tree.
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "generic-deepen,closed-metadata");

            // VerifyBuild/VerifyTestsByDefault: the runner already executed dotnet build +
            // dotnet test on the intermediate tree (RuntimeBehaviorTests = the STJ
            // semantics proof); any failure exits non-zero here.
            Assert.True(exit == 0,
                $"preprocess generic-deepen,closed-metadata failed (exit {exit}).\nstderr: {stderr}\nstdout tail: {stdout[^Math.Min(2500, stdout.Length)..]}");

            var closedRoot = Path.Combine(destination, ".cs2j-closed-metadata-src");
            Assert.True(Directory.Exists(closedRoot), "closed-metadata intermediate tree must exist");

            // Ownership: the Lib declares every fixture closure, so only the Lib gets
            // the registration file (App/Tests consume it through the project reference).
            // Generated files sit at the intermediate root and are wired into the owning
            // project's csproj via Compile Include/Link (GenericDeepen pattern).
            var libRegistration = Path.Combine(closedRoot, "_Cs2jClosedTypes.SampleClosedMetadataLib.cs");
            Assert.True(File.Exists(libRegistration),
                $"Lib registration file must exist at {libRegistration}");
            var libRegistrationText = await File.ReadAllTextAsync(libRegistration);

            Assert.Contains("public static void __cs2j_register_closed_types()", libRegistrationText);
            Assert.Contains(
                "typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>)",
                libRegistrationText);
            Assert.Contains(
                "typeof(global::System.Collections.Generic.Dictionary<string, global::System.Collections.Generic.List<int>>)",
                libRegistrationText);
            // The shared closure (SharedClosureDtoA/B) registers exactly once: one
            // typeof per key, no duplicate registration statements.
            Assert.Equal(1, CountOccurrences(
                libRegistrationText,
                "typeof(global::System.Collections.Generic.List<global::System.Collections.Generic.List<short>>)"));
            // The dynamic Bag<T> member is NOT registered (CM-DYN).
            Assert.DoesNotContain("__Cs2jClosedTypes_Bag", libRegistrationText);

            Assert.False(File.Exists(Path.Combine(
                    closedRoot, "_Cs2jClosedTypes.SampleClosedMetadataApp.cs")),
                "the App declares no closure, so it must not get a registration file");

            var libCsprojText = await File.ReadAllTextAsync(Path.Combine(
                closedRoot, "SampleClosedMetadataLib", "SampleClosedMetadataLib.csproj"));
            Assert.Contains("_Cs2jClosedTypes.SampleClosedMetadataLib.cs", libCsprojText);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}
