using System.Diagnostics;
using System.Text.Json;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// End-to-end CLI coverage for the C33 variance-audit pass against the
/// SampleVarianceAuditSolution fixture: standalone raw-tree audit (byte-identical
/// intermediate tree + report shape), audit vs strict semantics (VA030 gate),
/// Info suppression, the default chain staying untouched, and catalog listing.
/// </summary>
public class VarianceAuditProjectTests
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

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "SampleVarianceAuditSolution");

    private static string FixtureSolutionPath => Path.Combine(FixtureRoot, "SampleVarianceAuditSolution.sln");

    private static string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteMinimalProject(string projectDir, string source)
    {
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "Mini.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """);
        File.WriteAllText(Path.Combine(projectDir, "Safe.cs"), source);
    }

    private static async Task<JsonDocument> ReadReportAsync(string destination)
        => JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(destination, ".cs2j-variance-audit-src", "variance-audit-report.json")));

    private static bool IsUnderBuildOutput(string path, string root)
        => Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>The read-only pass must copy every input file byte for byte (design D1).</summary>
    private static void AssertIntermediateTreeMatchesSource(string sourceRoot, string intermediateRoot)
    {
        var sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsUnderBuildOutput(path, sourceRoot))
            .ToList();
        Assert.NotEmpty(sourceFiles);

        foreach (var sourceFile in sourceFiles)
        {
            var relative = Path.GetRelativePath(sourceRoot, sourceFile);
            var copied = Path.Combine(intermediateRoot, relative);
            Assert.True(File.Exists(copied), $"intermediate tree is missing '{relative}'");
            Assert.Equal(File.ReadAllBytes(sourceFile), File.ReadAllBytes(copied));
        }

        // No source file was added: the audit never emits a rewrite.
        var sourceCs = sourceFiles.Count(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var intermediateCs = Directory.EnumerateFiles(intermediateRoot, "*.cs", SearchOption.AllDirectories).Count();
        Assert.Equal(sourceCs, intermediateCs);
    }

    [Fact]
    public async Task StandaloneAudit_ReportsFixture_AndCopiesSourceVerbatim()
    {
        Assert.True(File.Exists(FixtureSolutionPath),
            "SampleVarianceAuditSolution must be copied to the test output directory");
        var workDir = CreateTempDir("cs2j-c33-audit");
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", FixtureSolutionPath, "-d", destination,
                "--pass", "variance-audit");

            Assert.True(exit == 0,
                $"preprocess --pass variance-audit failed (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");

            var intermediate = Path.Combine(destination, ".cs2j-variance-audit-src");
            Assert.True(Directory.Exists(intermediate), "variance-audit intermediate tree must exist");

            // Only the requested pass runs.
            var intermediateDirs = Directory
                .GetDirectories(destination, ".cs2j-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .ToArray();
            Assert.Single(intermediateDirs);
            Assert.Equal(".cs2j-variance-audit-src", intermediateDirs[0]);

            Assert.Contains("variance-audit:", stdout);

            using var document = await ReadReportAsync(destination);
            var root = document.RootElement;

            // A standalone raw-tree run carries neither E10 nor C02.
            var passPresent = root.GetProperty("passPresent");
            Assert.False(passPresent.GetProperty("generic").GetBoolean());
            Assert.False(passPresent.GetProperty("genericDeepen").GetBoolean());

            // Declarations: 3 out interfaces + 1 in interface + 1 in/1 out delegate.
            var declarations = root.GetProperty("declarationSites").EnumerateArray().ToList();
            Assert.Equal(6, declarations.Count);
            var totals = root.GetProperty("totals");
            Assert.Equal(3, totals.GetProperty("variantDeclarationsOutInterface").GetInt32());
            Assert.Equal(1, totals.GetProperty("variantDeclarationsInInterface").GetInt32());
            Assert.Equal(1, totals.GetProperty("variantDeclarationsOutDelegate").GetInt32());
            Assert.Equal(1, totals.GetProperty("variantDeclarationsInDelegate").GetInt32());

            // Every inventoried constraint kind is present in the fixture.
            var kinds = root.GetProperty("constraintSites").EnumerateArray()
                .Select(site => site.GetProperty("kind").GetString())
                .ToHashSet();
            foreach (var expected in new[] { "struct", "unmanaged", "new", "notnull", "default", "class" })
            {
                Assert.Contains(expected, kinds);
            }

            // At least one covariant and one contravariant conversion site.
            Assert.True(totals.GetProperty("varianceConversionsCovariant").GetInt32() >= 1);
            Assert.True(totals.GetProperty("varianceConversionsContravariant").GetInt32() >= 1);

            var diagnostics = stdout + stderr;
            Assert.Contains("VA010", diagnostics);
            Assert.Contains("VA011", diagnostics);

            AssertIntermediateTreeMatchesSource(FixtureRoot, intermediate);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task AuditMode_IsNonBlocking_WarningsDoNotAbort()
    {
        var workDir = CreateTempDir("cs2j-c33-nonblocking");
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", FixtureSolutionPath, "-d", destination,
                "--pass", "variance-audit");

            Assert.True(exit == 0, $"audit mode must not block (exit {exit}).\nstderr: {stderr}");
            Assert.Contains("warning VA", stderr);
            Assert.DoesNotContain("error VA", stderr);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task StrictMode_AbortsWithVA030_WhenUnsafeSitesExist()
    {
        var workDir = CreateTempDir("cs2j-c33-strict");
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", FixtureSolutionPath, "-d", destination,
                "--pass", "variance-audit", "--variance-strict");

            Assert.NotEqual(0, exit);
            var output = stdout + stderr;
            Assert.Contains("VA030", output);
            Assert.Contains("unsafe site(s)", output);
            // At least one unsafe category is escalated to Error.
            Assert.Contains("error VA010", output);
            // The gate is a single summary code that CI can match.
            var gateLine = output.Split('\n').First(line => line.Contains("VA030"));
            Assert.Contains("VA027", gateLine);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task StrictMode_Succeeds_OnZeroUnsafeProject()
    {
        var workDir = CreateTempDir("cs2j-c33-safe");
        var sourceDir = Path.Combine(workDir, "Safe");
        var destination = Path.Combine(workDir, "out");

        try
        {
            WriteMinimalProject(sourceDir, """
            namespace Safe;

            public interface IProducer<out T>
            {
                T Produce();
            }

            public sealed class Producer : IProducer<string>
            {
                public string Produce() => "value";
            }

            public static class Helpers
            {
                public static T Make<T>() where T : class, new() => new T();

                public static void RequireNotNull<T>(T value) where T : notnull => _ = value;
            }
            """);

            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", sourceDir, "-d", destination,
                "--pass", "variance-audit", "--variance-strict");

            Assert.True(exit == 0,
                $"an Info-only project must pass strict (exit {exit}).\nstderr: {stderr}\nstdout: {stdout}");
            Assert.DoesNotContain("VA030", stdout + stderr);
            Assert.Contains("0 unsafe", stdout);
            Assert.True(File.Exists(Path.Combine(
                destination, ".cs2j-variance-audit-src", "variance-audit-report.json")));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoReportInfo_SuppressesInfoDiagnostics_ButKeepsReportSites()
    {
        var workDir = CreateTempDir("cs2j-c33-noinfo");
        var destination = Path.Combine(workDir, "out");

        try
        {
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", FixtureSolutionPath, "-d", destination,
                "--pass", "variance-audit", "--variance-no-report-info");

            Assert.True(exit == 0, $"exit {exit}.\nstderr: {stderr}");
            var output = stdout + stderr;
            Assert.DoesNotContain("VA001", output);
            Assert.DoesNotContain("VA013", output);

            using var document = await ReadReportAsync(destination);
            var root = document.RootElement;
            Assert.False(root.GetProperty("options").GetProperty("reportInfo").GetBoolean());
            // The report keeps every site regardless of the diagnostic-stream filter.
            Assert.Equal(6, root.GetProperty("declarationSites").GetArrayLength());
            Assert.True(root.GetProperty("useSites").GetArrayLength() > 0);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [SlowE2EFact(40.74)]
    public async Task DefaultConvertProject_RunsVarianceAuditIntermediateTree()
    {
        var workDir = CreateTempDir("cs2j-c33-default");
        var sourceDir = Path.Combine(workDir, "Demo");
        var destination = Path.Combine(workDir, "java");

        try
        {
            // --no-verify-* only skips the default-on build/test verification cycle;
            // the pass chain itself still runs, which is what this assertion pins.
            WriteMinimalProject(sourceDir, "namespace Demo; public class Sample { public int M() => 1; }");

            var (exit, _, stderr) = await RunCliAsync(
                "convert-project", "-s", sourceDir, "-d", destination,
                "--no-cache", "--no-verify-build", "--no-verify-tests");

            Assert.True(exit == 0, $"convert-project failed (exit {exit}).\nstderr: {stderr}");
            Assert.True(Directory.Exists(Path.Combine(destination, ".cs2j-variance-audit-src")),
                "variance-audit is default-on (all-on policy) and must run in the default chain");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public async Task ListPasses_ShowsVarianceAudit_ImplementedOnByDefault_WithC33Rules()
    {
        var (exit, stdout, _) = await RunCliAsync("preprocess", "--list-passes");
        Assert.Equal(0, exit);

        var line = stdout.Split('\n').First(l => l.Contains("variance-audit"));
        Assert.Contains("C33", line);
        Assert.Contains(" on ", line);
        Assert.Contains("Implemented", line);
    }
}