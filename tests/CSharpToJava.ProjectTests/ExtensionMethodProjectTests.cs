using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end coverage for the extension-methods pass (C19 M1) on the
/// SampleExtensionMethodsSolution fixture: a single --pass run rewrites user
/// extension sugar into global-qualified static calls across three projects,
/// leaves LINQ sugar and clean files byte-identical, and the runner's
/// build+test verification proves runtime semantics are preserved.
/// </summary>
public class ExtensionMethodProjectTests
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
    public async Task Solution_ExtensionMethodsPass_ExplicitizesUserCallsAndBuildsAndTests()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtensionMethodsSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtensionMethodsSolution must be copied to the test output directory");
        var solutionPath = Path.Combine(fixture, "SampleExtensionMethodsSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            // --pass selects exactly this pass (all other passes disabled);
            // the runner also performs dotnet build + dotnet test on the
            // intermediate tree, so exit 0 already proves the rewritten code
            // compiles and keeps runtime semantics.
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "extension-methods");

            Assert.True(exit == 0,
                $"preprocess --pass extension-methods failed (exit {exit}).\nstderr: {stderr}\nstdout tail: {stdout[^Math.Min(2000, stdout.Length)..]}");

            var intermediateRoot = Path.Combine(destination, ".cs2j-extension-methods-src");
            Assert.True(Directory.Exists(intermediateRoot),
                "extension-methods intermediate tree must exist");

            var appProgram = await File.ReadAllTextAsync(
                Directory.GetFiles(intermediateRoot, "Program.cs", SearchOption.AllDirectories)
                    .Single(p => p.Contains("SampleExtApp", StringComparison.OrdinalIgnoreCase)));

            // Basic + multi-arg user calls become global-qualified static calls.
            Assert.Contains("global::SampleExtLib.Text.TextExtensions.Shout(\"hi\")", appProgram);
            Assert.Contains("global::SampleExtLib.Text.TextExtensions.Repeat(\"ab\", 2)", appProgram);
            // All-extension chain converges bottom-up (Echo generic inference
            // stays implicit).
            Assert.Contains(
                "global::SampleExtLib.Text.TextExtensions.Shout(global::SampleExtLib.Text.TextExtensions.Echo(\"go\"))",
                appProgram);
            Assert.DoesNotContain("Echo<", appProgram);

            // Instance member of the same name (zero args) is untouched; the
            // one-arg form resolves through the extension and is explicitized.
            Assert.Contains("counter.Report()", appProgram);
            Assert.Contains("global::SampleExtLib.Shapes.CounterExtensions.Report(counter, 10)", appProgram);
            // Cross-project enumerable extension chain.
            Assert.Contains(
                "global::SampleExtLib.Collections.CollectionExtensions.JoinWith(global::SampleExtLib.Collections.CollectionExtensions.RequireNonEmpty(new[] { \"x\", \"y\" }), \",\")",
                appProgram);

            // LINQ sugar stays reduced: no global Enumerable/Queryable
            // qualification anywhere in the app.
            Assert.DoesNotContain("global::System.Linq", appProgram);
            Assert.Contains(".Select(n => n * 2).Sum()", appProgram);

            // The fixture's own xunit project is rewritten too; the runner
            // executed it successfully, and its text carries the static form.
            var runtimeTests = await File.ReadAllTextAsync(
                Directory.GetFiles(intermediateRoot, "RuntimeBehaviorTests.cs", SearchOption.AllDirectories).Single());
            Assert.Contains("global::SampleExtLib.Text.TextExtensions.Shout(\"a\")", runtimeTests);
            Assert.Contains(
                "global::SampleExtLib.Collections.CollectionExtensions.RequireNonEmpty(missing)",
                runtimeTests);

            // Clean file (no sugar): byte-copied including its UTF-8 BOM.
            var sourceClean = Path.Combine(fixture, "SampleExtApp", "CleanMarker.cs");
            var intermediateClean = Path.Combine(intermediateRoot, "SampleExtApp", "CleanMarker.cs");
            var sourceBytes = await File.ReadAllBytesAsync(sourceClean);
            var intermediateBytes = await File.ReadAllBytesAsync(intermediateClean);
            Assert.Equal(sourceBytes, intermediateBytes);
            Assert.True(
                intermediateBytes.Length >= 3
                && intermediateBytes[0] == 0xEF && intermediateBytes[1] == 0xBB && intermediateBytes[2] == 0xBF,
                "CleanMarker.cs must keep its UTF-8 BOM after the byte-copy path");

            // C19 M2: ref/scoped-ref Span receivers, null-safe receivers,
            // null-conditional lifting temps, and method-group capture live in
            // a dedicated file (lift/temp files use whole-file normalization).
            var m2Scenarios = await File.ReadAllTextAsync(
                Directory.GetFiles(intermediateRoot, "M2Scenarios.cs", SearchOption.AllDirectories)
                    .Single(p => p.Contains("SampleExtApp", StringComparison.OrdinalIgnoreCase)));

            // EM-REF: ref keyword emitted for both 'this ref' and
            // 'this scoped ref' (scoped itself is metadata-only).
            Assert.Contains(
                "global::SampleExtLib.Spans.SpanExtensions.IncrementFirst(ref values)",
                m2Scenarios);
            Assert.Contains(
                "global::SampleExtLib.Spans.SpanExtensions.Sum(ref values)",
                m2Scenarios);

            // Nullable receiver call: no ref keyword, ordinary static form.
            Assert.Contains(
                "global::SampleExtLib.NullGuards.NullGuardExtensions.IsNull(missing)",
                m2Scenarios);

            // EM-COND: single-evaluation temps around the lifted spine.
            Assert.Contains("_cs2j_emc", m2Scenarios);
            Assert.Contains(
                "global::SampleExtLib.NullGuards.NullGuardExtensions.LengthOrNull(_cs2j_emc",
                m2Scenarios);
            Assert.Contains("default(int?", m2Scenarios);

            // EM-GROUP: direct receiver capture lambda.
            Assert.Contains(
                "() => global::SampleExtLib.Delegates.GreetingExtensions.Greet(name)",
                m2Scenarios);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task Solution_Statistics_SplitInCompilationUserFromExternal()
    {
        // M3 (design §4.2-1/2): every host in this fixture is source inside the
        // merged compilation, so the summary must report user in-compilation
        // sites, zero support-library sites and zero external hosts.
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtensionMethodsSolution");
        var solutionPath = Path.Combine(fixture, "SampleExtensionMethodsSolution.sln");
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-stats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "extension-methods", "--verbose");

            Assert.True(exit == 0,
                $"preprocess failed (exit {exit}).\nstderr: {stderr}");

            var match = System.Text.RegularExpressions.Regex.Match(
                stdout,
                @"(?<user>\d+) user \+ 0 support in-compilation, 0 external hosts \[0 mapped/0 unmapped\]");
            Assert.True(match.Success,
                $"summary must show user-only in-compilation split. stdout tail: {stdout[^Math.Min(1000, stdout.Length)..]}");
            Assert.True(int.Parse(match.Groups["user"].Value) > 0,
                "cross-project Lib/App sites must be counted as user in-compilation");
            Assert.DoesNotContain("CS2J1203", stderr);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
