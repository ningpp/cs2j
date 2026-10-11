using System.Diagnostics;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C19 M3 end-to-end coverage for external-metadata host grading (design
/// §4.2-3, EM-EXTERNAL). The fixture's extension host is packed into a local
/// NuGet feed by this harness and consumed via PackageReference, so it reaches
/// the merged binding compilation purely as metadata:
/// - the unmapped synthetic host site is still explicitized and reports
///   CS2J1203 (Warning → exit 0);
/// - the BCL System.MemoryExtensions site is graded Mapped via the shipped
///   TypeMappings.json and stays silent;
/// - the summary line carries the external [mapped/unmapped] counters.
/// </summary>
public class ExtensionMethodExternalHostProjectTests
{
    private static string CliPath => TestCliPaths.LocateCli();

    [Fact]
    public async Task PackageMetadataHost_UnmappedSite1203_MappedBclSiteSilent()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtExternalHostSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtExternalHostSolution must be copied to the test output directory");

        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-em-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        // The solution subtree is the ONLY thing preprocess may see; the
        // packaging project and feed live as siblings so the host sources can
        // never enter the merged compilation.
        var repoDir = Path.Combine(workDir, "repo");
        var hostProjectDir = Path.Combine(workDir, "host-project");
        var localFeed = Path.Combine(workDir, "local-feed");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(hostProjectDir);
        CopyDirectory(fixture, repoDir);

        try
        {
            // 1) Materialize the metadata host as a local NuGet package.
            await File.WriteAllTextAsync(
                Path.Combine(hostProjectDir, "MetadataStringExtensions.cs"),
                """
                namespace ExtHosts
                {
                    public static class MetadataStringExtensions
                    {
                        public static string ShoutMetadata(this string s) => s.ToUpperInvariant() + "!";
                    }
                }
                """);
            await File.WriteAllTextAsync(
                Path.Combine(hostProjectDir, "SampleExtMetadataHost.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <PackageId>SampleExtMetadataHost</PackageId>
                    <PackageVersion>1.0.0</PackageVersion>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                </Project>
                """);

            RunDotnet($"pack \"{Path.Combine(hostProjectDir, "SampleExtMetadataHost.csproj")}\" -c Release -o \"{localFeed}\" --nologo", workDir);
            Assert.True(
                Directory.Exists(localFeed) && Directory.GetFiles(localFeed, "SampleExtMetadataHost*.nupkg").Length > 0,
                "host package must be packed into the local feed");

            // Restore runs inside the copied intermediate tree, so the feed
            // path in nuget.config has to be absolute.
            var nugetConfigPath = Path.Combine(repoDir, "nuget.config");
            var nugetConfig = await File.ReadAllTextAsync(nugetConfigPath);
            await File.WriteAllTextAsync(nugetConfigPath,
                nugetConfig.Replace("./local-feed", localFeed.Replace('\\', '/')));

            // 2) Run the extension-methods pass on the app-only solution.
            var destination = Path.Combine(workDir, "out");
            var solutionPath = Path.Combine(repoDir, "SampleExtExternalHostSolution.sln");
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "extension-methods", "--verbose");

            Assert.True(exit == 0,
                $"CS2J1203 is Warning-grade; preprocess must exit 0.\nstderr: {stderr}\nstdout tail: {stdout[^Math.Min(2000, stdout.Length)..]}");

            // 3) Both metadata-host sites are explicitized.
            var intermediateRoot = Path.Combine(destination, ".cs2j-extension-methods-src");
            var program = await File.ReadAllTextAsync(
                Directory.GetFiles(intermediateRoot, "Program.cs", SearchOption.AllDirectories).Single());

            Assert.Contains(
                "global::ExtHosts.MetadataStringExtensions.ShoutMetadata(\"hi\")",
                program);
            Assert.Contains("global::System.MemoryExtensions.AsSpan(\"abc\")", program);

            // 4) Exactly one CS2J1203 — the synthetic host; MemoryExtensions is
            //    graded Mapped and produces none.
            var lines1203 = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("CS2J1203", StringComparison.Ordinal))
                .ToList();
            Assert.Single(lines1203);
            Assert.Contains("ExtHosts.MetadataStringExtensions", lines1203[0]);
            Assert.DoesNotContain("MemoryExtensions", string.Join('\n', lines1203));

            // 5) Summary line: 2 external hosts, 1 mapped / 1 unmapped.
            Assert.Contains("2 external hosts [1 mapped/1 unmapped]", stdout);

            // 6) Verbose EM001 detail is emitted for the external sites.
            Assert.Contains("EM001", stderr);
            Assert.Contains("classification=ExternalMetadata", stderr);
        }
        finally
        {
            // Best-effort cleanup; read-only package files may linger.
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

    private static void RunDotnet(string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(10 * 60 * 1000);
        Assert.True(process.HasExited && process.ExitCode == 0,
            $"dotnet {arguments} failed (exit {(process.HasExited ? process.ExitCode : -1)}): {stderr}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
