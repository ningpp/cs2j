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

    [SlowE2EFact(11.88)]
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

            // CM-LINK (M4b): D8 serializer entry statements in the consumer projects
            // invoke the Lib holder's idempotent bootstrap before the call. The App
            // owns no records and still gets no registration file — it reaches the
            // holder through its project reference (holder is public).
            const string libGuard =
                "global::__Cs2jClosedTypes_SampleClosedMetadataLib.__cs2j_register_closed_types();";
            var programText = await File.ReadAllTextAsync(
                Path.Combine(closedRoot, "SampleClosedMetadataApp", "Program.cs"));
            // Four serializer call statements (the two nested Deserialize(Serialize(...))
            // expressions share one enclosing statement → one guard each).
            Assert.Equal(4, CountOccurrences(programText, libGuard));

            var behaviorTestsText = await File.ReadAllTextAsync(
                Path.Combine(closedRoot, "SampleClosedMetadataTests", "RuntimeBehaviorTests.cs"));
            // Three facts × two serializer statements each, plus the M4c surface
            // fact: embedded-if nested pair (1 statement) + switch-arm + follow-up
            // Deserialize = 3 statements → 9 guards.
            Assert.Equal(9, CountOccurrences(behaviorTestsText, libGuard));

            // The bootstrap itself is idempotent: lock double-check flag in the holder.
            Assert.Contains("private static bool __cs2j_bootstrapped;", libRegistrationText);

            // M4c §5.4 gate: the rewritten intermediate tree reports zero residuals
            // (rules 1/3/4/6 recompiled gate alongside the rules 2/5 content gate).
            Assert.Contains("0 residuals", stdout);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [SlowE2EFact(300)]
    public async Task RefOutInSolution_DefaultChain_RegistrationNeverTargetsInjectedSupportProject()
    {
        // Regression: closed constructions declared by the injected Cs2jRefOutIn
        // support library (e.g. ByRef<Color>) used to be owned by the support
        // project itself. The generated registration then named user types
        // (SampleRefOutInLib.Color) inside Cs2jRefOutIn, which cannot reference
        // user projects, so the closed-metadata verification build failed with
        // CS0400. Ownership must instead fall back to the discovery-site user
        // project (D6 framework branch).
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleRefOutInSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleRefOutInSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleRefOutIn.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-cm-roi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination);

            Assert.True(exit == 0,
                $"default-chain preprocess failed (exit {exit}).\nstderr: {stderr}\nstdout tail: {stdout[^Math.Min(2500, stdout.Length)..]}");

            var closedRoot = Path.Combine(destination, ".cs2j-closed-metadata-src");
            Assert.True(Directory.Exists(closedRoot), "closed-metadata intermediate tree must exist");

            // The discovery-site user project owns the registration …
            Assert.True(File.Exists(Path.Combine(closedRoot, "_Cs2jClosedTypes.SampleRefOutInLib.cs")),
                "the ByRef<...> closures must be registered in the discovery-site user project");
            // … and the symbol-source support project never receives one.
            Assert.False(File.Exists(Path.Combine(closedRoot, "_Cs2jClosedTypes.Cs2jRefOutIn.cs")),
                "the injected Cs2jRefOutIn support project must not receive a closed-type registration file");
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
