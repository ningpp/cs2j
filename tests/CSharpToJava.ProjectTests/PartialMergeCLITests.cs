using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C20 CLI end-to-end (design §7.3): partial-merge flipped to default-on
/// on 2026-10-11 under the all-preprocessors-default-on policy;
/// --disable-pass opts out; the standalone `preprocess --pass partial-merge`
/// selection runs exactly that pass. Both executions leave the
/// .cs2j-partial-merge-src intermediate tree.
/// </summary>
[Collection("SamplePartialSolution")]
public sealed class PartialMergeCLITests
{
    private static string CliDll
        => Path.Combine(AppContext.BaseDirectory, "CSharpToJava.CLI.dll");

    private static string FixtureSolution
        => Path.Combine(AppContext.BaseDirectory, "SamplePartialSolution", "SamplePartialSolution.slnx");

    /// <summary>
    /// Workspace-local scratch (see PartialMergeProjectTests.NewScratchRoot
    /// rationale: the machine janitor reaps %TEMP% directories of exited children).
    /// </summary>
    private static string NewWorkRoot(string tag)
        => Path.Combine(AppContext.BaseDirectory, ".tmp-c20", tag + "-" + Guid.NewGuid().ToString("N"));

    private static (int ExitCode, string Output) RunCli(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(CliDll);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }

    [Fact]
    public void Default_Plan_Lists_PartialMerge_As_DefaultOn()
    {
        var (exitCode, output) = RunCli("preprocess", "--list-passes");
        Assert.Equal(0, exitCode);

        // Match the id field itself: later rows reference partial-merge in their
        // run-after column and would also contain the substring.
        var line = Assert.Single(output.Split('\n')
            .Where(line => line.TrimStart().StartsWith("partial-merge ", StringComparison.Ordinal)));
        Assert.Contains(" on ", line, StringComparison.Ordinal);
    }

    [SlowE2EFact(184.70)]
    public void EnablePass_Executes_And_Creates_Intermediate_Directory()
    {
        var workRoot = NewWorkRoot("cli");
        try
        {
            var (exitCode, output) = RunCli(
                "preprocess", "-s", FixtureSolution, "-d", workRoot,
                "--enable-pass", "partial-merge",
                // Unrelated gates: unsafe-ffm rejects user-defined ref structs;
                // equality-concat cannot classify sites in the chain-attached
                // Cs2jUnsignedMath support project. Those shapes are covered at
                // unit/standalone level.
                "--disable-pass", "unsafe-ffm",
                "--disable-pass", "equality-concat");

            Assert.True(exitCode == 0, output);

            Assert.Contains(
                Directory.GetDirectories(workRoot),
                directory => Path.GetFileName(directory) == ".cs2j-partial-merge-src");
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Standalone_Preprocess_Pass_Reaches_PartialMerge()
    {
        var workRoot = NewWorkRoot("standalone");
        try
        {
            var (exitCode, output) = RunCli(
                "preprocess", "-s", FixtureSolution, "-d", workRoot,
                "--pass", "partial-merge");

            Assert.True(exitCode == 0, output);
            Assert.Contains("partial-merge", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(
                Directory.GetDirectories(workRoot),
                directory => Path.GetFileName(directory) == ".cs2j-partial-merge-src");
        }
        finally
        {
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }
}
