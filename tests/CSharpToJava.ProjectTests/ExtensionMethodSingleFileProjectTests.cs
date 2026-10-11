using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C19 M3 project-level coverage for CS2J1290 (design §4.2-5/§6): a loose
/// directory without any csproj runs the pass in single-file conservative
/// mode — an extension-like call that fails overload resolution (here a
/// class-constrained generic invoked on an int) cannot be distinguished from
/// a missing project/NuGet reference, so the site is left unchanged with a
/// Warning that is never strict-escalated and the run still exits 0.
/// </summary>
public class ExtensionMethodSingleFileProjectTests
{
    private static string CliPath => TestCliPaths.LocateCli();

    [Fact]
    public async Task LooseDirectory_FailedExtensionCandidate_Warns1290AndKeepsSite()
    {
        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-1290-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "Loose.cs"),
                """
                class P
                {
                    string Run()
                    {
                        int x = 1;
                        return x.NeedsClass();
                    }
                }

                static class ConstraintExtensions
                {
                    public static string NeedsClass<T>(this T value) where T : class => "";
                }
                """);

            var destination = Path.Combine(workDir, "out");
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{CliPath}\" preprocess -s \"{workDir}\" -d \"{destination}\" "
                    + "--pass extension-methods --verbose",
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
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.True(process.ExitCode == 0,
                $"CS2J1290 is Warning-grade (never strict); exit must be 0.\nstderr: {stderr}");

            // Warning identifies the site and the conservative rationale.
            Assert.Contains("CS2J1290", stderr);
            Assert.Contains("x.NeedsClass()", stderr);
            Assert.DoesNotContain("CS2J1202", stderr);

            // Summary carries the single-file counter.
            Assert.Contains("1 single-file deferred", stdout);

            // Site is left unchanged.
            var output = await File.ReadAllTextAsync(
                Path.Combine(destination, ".cs2j-extension-methods-src", "Loose.cs"));
            Assert.Contains("x.NeedsClass()", output);
            Assert.DoesNotContain("ConstraintExtensions.NeedsClass", output);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
