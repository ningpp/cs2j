using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C19 M3 serial-chain coverage (design §9 M3 scope #7): each injected support
/// library solution is first run through its own pass and then through
/// extension-methods on the produced intermediate tree. Anchors:
/// - both runs succeed; for solutions whose support-pass output is itself
///   build-verifiable (unsigned / ref-out-in V2), the extension-methods
///   intermediate tree still builds through the runner's verification;
/// - the injected support-library directory survives into the
///   extension-methods intermediate tree;
/// - the C19 pass introduces no binding warnings (CS2J1201/1202/1203) and no
///   CS2J1220 residuals on these merged trees.
///
/// The array-method sample is the documented exception: its support pass
/// intentionally emits flattened multidim foreach shapes that are not
/// independently compilable until later chain passes (see the textual-only
/// assertions in ArrayMethodProjectTests), so verification is skipped for
/// that serial pair while the C19-specific anchors still apply.
/// </summary>
public class ExtensionMethodSupportLibrarySerialProjectTests
{
    public sealed record SerialCase(
        string FixtureFolder,
        string SolutionFileName,
        string SupportPassId,
        string SupportIntermediateDir,
        string SupportLibraryDirName,
        bool ExtensionPassBuildVerifiable);

    public static IEnumerable<object[]> Cases => new[]
    {
        new object[]
        {
            new SerialCase(
                "SampleArraySolution",
                "SampleArray.sln",
                "array-method",
                ".cs2j-array-src",
                "Cs2jArray",
                ExtensionPassBuildVerifiable: false),
        },
        new object[]
        {
            new SerialCase(
                "SampleUnsignedSolution",
                "SampleUnsignedSolution.sln",
                "unsigned",
                ".cs2j-unsigned-src",
                "Cs2jUnsignedMath",
                ExtensionPassBuildVerifiable: true),
        },
        new object[]
        {
            new SerialCase(
                "SampleRefOutInV2Solution",
                "SampleRefOutInV2.sln",
                "ref-out-in",
                ".cs2j-refoutin-src",
                "Cs2jRefOutIn",
                ExtensionPassBuildVerifiable: true),
        },
    };

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

    [SlowE2ETheory(17.78)]
    [MemberData(nameof(Cases))]
    public async Task SupportPassThenExtensionMethods_ChainIsGreen(SerialCase testCase)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, testCase.FixtureFolder);
        Assert.True(Directory.Exists(fixture),
            $"{testCase.FixtureFolder} must be copied to the test output directory");

        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-serial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var supportOut = Path.Combine(workDir, "support-out");
            var extensionOut = Path.Combine(workDir, "extension-out");

            // 1) The support library's own pass.
            var (supportExit, _, supportStderr) = await RunCliAsync(
                "preprocess",
                "-s", Path.Combine(fixture, testCase.SolutionFileName),
                "-d", supportOut,
                "--pass", testCase.SupportPassId, "--verbose");
            Assert.True(supportExit == 0,
                $"[{testCase.FixtureFolder}] support pass failed.\nstderr: {supportStderr}");

            var supportIntermediate = Path.Combine(supportOut, testCase.SupportIntermediateDir);
            Assert.True(Directory.Exists(supportIntermediate),
                $"[{testCase.FixtureFolder}] support intermediate tree missing");
            Assert.True(
                Directory.GetDirectories(
                        supportIntermediate, testCase.SupportLibraryDirName, SearchOption.AllDirectories).Length > 0,
                $"[{testCase.FixtureFolder}] injected {testCase.SupportLibraryDirName} anchor missing after support pass");

            // 2) extension-methods on the support-pass output.
            var intermediateSln = Path.Combine(supportIntermediate, testCase.SolutionFileName);
            Assert.True(File.Exists(intermediateSln),
                $"[{testCase.FixtureFolder}] intermediate sln missing: {intermediateSln}");

            var extensionArgs = new List<string>
            {
                "preprocess",
                "-s", intermediateSln,
                "-d", extensionOut,
                "--pass", "extension-methods", "--verbose",
            };
            if (!testCase.ExtensionPassBuildVerifiable)
            {
                extensionArgs.Add("--no-verify-build");
                extensionArgs.Add("--no-verify-tests");
            }

            var (exit, stdout, stderr) = await RunCliAsync(extensionArgs.ToArray());
            Assert.True(exit == 0,
                $"[{testCase.FixtureFolder}] extension-methods on support output failed (exit {exit}).\nstderr: {stderr}");

            // Anchor survives the second pass.
            var extensionIntermediate = Path.Combine(
                extensionOut, ".cs2j-extension-methods-src");
            Assert.True(
                Directory.GetDirectories(
                        extensionIntermediate, testCase.SupportLibraryDirName, SearchOption.AllDirectories).Length > 0,
                $"[{testCase.FixtureFolder}] {testCase.SupportLibraryDirName} must survive the extension-methods pass");

            // C19 cleanliness: no unresolved bindings, no unmapped external
            // hosts, no residuals introduced by the serial run.
            Assert.Contains("extension-methods:", stdout);
            Assert.DoesNotContain("CS2J1201", stderr);
            Assert.DoesNotContain("CS2J1202", stderr);
            Assert.DoesNotContain("CS2J1203", stderr);
            Assert.DoesNotContain("CS2J1220", stderr);
            Assert.Contains("0 residuals)", stdout);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
}
