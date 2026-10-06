using System.Diagnostics;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C25 M2 project-level gate (design §7.2 assertion matrix) against the
/// SampleDynamicDesugarSolution fixture: default-off byte invariance, Audit
/// report-only counts, Rewrite with the bundled Cs2jDynamicAudit.json (full
/// verify build + test), strict escalation, and the DS030 unregistered-residual
/// negative. Intermediate roots live under the (short) temp path to avoid the
/// testhost Win32 206 deep-path failure.
/// </summary>
public class DynamicDesugarSolutionProjectTests
{
    private const string IntermediateDirName = ".cs2j-dynamic-desugar-src";
    private const string ConditionalDirName = ".cs2j-conditional-src";

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

    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SampleDynamicDesugarSolution");

    private static async Task<(int ExitCode, string Output)> RunCliAsync(params string[] args)
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
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask + await stderrTask);
    }

    private static string TempDir(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

    private static string ReportPath(string destDir)
        => Path.Combine(destDir, IntermediateDirName, "dynamic-audit-report.json");

    /// <summary>Fixture inputs (bin/obj/TestResults never ship as content).</summary>
    private static IEnumerable<(string Relative, string Full)> FixtureInputs(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(full => (Relative: full[root.Length..].TrimStart('\\', '/'), Full: full))
            .Where(t => !t.Relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !t.Relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !t.Relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !t.Relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !t.Relative.Contains(Path.DirectorySeparatorChar + "TestResults" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static async Task AssertInputsByteEqual(string sourceRoot, string intermediateRoot)
    {
        foreach (var (relative, full) in FixtureInputs(sourceRoot))
        {
            var copied = Path.Combine(intermediateRoot, relative);
            Assert.True(File.Exists(copied), $"fixture input missing from intermediate tree: {relative}");
            Assert.Equal(
                Convert.ToHexString(await File.ReadAllBytesAsync(full)),
                Convert.ToHexString(await File.ReadAllBytesAsync(copied)));
        }
    }

    // dynamic is a contextual keyword: this repo's Roslyn lexes it as an
    // IdentifierToken with text "dynamic" (same predicate as DynamicResidualGate).
    // String literals are one StringLiteralToken and comments are trivia, so
    // neither is counted by DescendantTokens.
    private static int CountDynamicKeywordTokens(string code)
        => CSharpSyntaxTree.ParseText(code).GetRoot().DescendantTokens()
            .Count(t => t.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.IdentifierToken
                && t.Text == "dynamic");

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, destination));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, destination), overwrite: true);
        }
    }

    // ---------- §7.2 断言矩阵 ----------

    [Fact]
    public async Task OtherPassExplicitlySelected_OutputsBytesUntouched_AndNoDynamicIntermediateTree()
    {
        // Audit flipped default-on for the CHAIN (2026-10-06); explicit
        // single-pass selection still runs only that pass, so no dynamic tree.
        var destDir = TempDir("cs2j-dd-off-");
        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "conditional",
                "--no-verify-build", "--no-verify-tests",
                "-s", FixtureDir, "-d", destDir);

            Assert.True(exit == 0, output);
            Assert.False(Directory.Exists(Path.Combine(destDir, IntermediateDirName)));

            // The conditional pass is byte-copy on this clean fixture: every
            // shipped input (including the precompiled boundary dll) is identical.
            AssertInputsByteEqual(FixtureDir, Path.Combine(destDir, ConditionalDirName));
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Audit_ReportOnly_BytesEqual_AndReportCountsPinned()
    {
        var destDir = TempDir("cs2j-dd-audit-");
        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "dynamic-desugar",
                "--dynamic-mode", "Audit", "--dynamic-report-only",
                "--no-verify-build", "--no-verify-tests",
                "-s", FixtureDir, "-d", destDir);

            Assert.True(exit == 0, output);
            Assert.DoesNotContain("error ", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mode=Audit", output);

            var intermediateRoot = Path.Combine(destDir, IntermediateDirName);
            AssertInputsByteEqual(FixtureDir, intermediateRoot);

            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            var root = document.RootElement;
            Assert.Equal("audit", root.GetProperty("mode").GetString());

            var totals = root.GetProperty("totals");
            Assert.Equal(7, totals.GetProperty("filesScanned").GetInt32());
            Assert.Equal(43, totals.GetProperty("sites").GetInt32());
            Assert.Equal(11, totals.GetProperty("declarationsClassified").GetInt32());
            Assert.Equal(4, totals.GetProperty("provenDeclarations").GetInt32());
            Assert.Equal(3, totals.GetProperty("unprovableSites").GetInt32());
            Assert.Equal(0, totals.GetProperty("residualSites").GetInt32());

            // Pinned per-kind distribution (audit sees the input inventory; the
            // P1 file contributes declarations + use-sites before concretization).
            var expectedKinds = new Dictionary<string, int>
            {
                ["dynamicLocalDecl"] = 7,
                ["dynamicParameter"] = 1,
                ["dynamicReturn"] = 2,
                ["dynamicFieldOrProperty"] = 1,
                ["dynamicInvocation"] = 7,
                ["dynamicMemberAccess"] = 3,
                ["dynamicElementAccess"] = 0,
                ["dynamicOperation"] = 6,
                ["dynamicConversion"] = 11,
                ["expandoOrDynamicObject"] = 2,
                ["jsonDynamic"] = 0,
                ["runtimeBinderApi"] = 0,
                ["dynamicAttributeEntry"] = 3,
            };
            var kinds = totals.GetProperty("sitesByKind");
            foreach (var (kind, count) in expectedKinds)
            {
                Assert.True(kinds.TryGetProperty(kind, out var value), $"missing kind {kind}");
                Assert.Equal(count, value.GetInt32());
            }
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_BundledManifest_VerifyBuildAndTestsGreen_AndP1StaticallyBound()
    {
        // Full path (no --no-verify-*): the intermediate tree is restored, built
        // and xUnit-tested at -c Release before the residual gate completes.
        var destDir = TempDir("cs2j-dd-rw-");
        try
        {
            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "dynamic-desugar",
                "--dynamic-mode", "Rewrite",
                "-s", FixtureDir, "-d", destDir);

            Assert.True(exit == 0, output);
            Assert.Contains("Verifying build", output);
            Assert.Contains("Verifying tests", output);

            // Run statistics line: 43 input sites, all 28 residuals manifest
            // registered, 4 declarations concretized, nothing left over.
            Assert.Contains("43 site(s)", output);
            Assert.Contains("28 manifest-suppressed", output);
            Assert.Contains("4 declaration(s) concretized", output);
            Assert.Contains("0 residual", output);
            Assert.Contains("mode=Rewrite", output);
            Assert.DoesNotContain("DS030", output);
            Assert.DoesNotContain("DS061", output);
            Assert.DoesNotContain("error ", output, StringComparison.OrdinalIgnoreCase);

            var ds060 = System.Text.RegularExpressions.Regex.Count(output, "DS060");
            Assert.Equal(28, ds060);

            using var document = JsonDocument.Parse(File.ReadAllBytes(ReportPath(destDir)));
            Assert.Equal("rewrite", document.RootElement.GetProperty("mode").GetString());

            // The four P1 declarations are concretized and every use-site is
            // statically rebound: zero dynamic keyword tokens remain (comments
            // mentioning "dynamic" do not count) and the concrete types are fixed.
            var p1 = await File.ReadAllTextAsync(Path.Combine(
                destDir, IntermediateDirName, "SampleDynamicDesugarLib", "P1Concretizations.cs"));
            Assert.Equal(0, CountDynamicKeywordTokens(p1));
            Assert.Contains("SealedBuilder builder = new SealedBuilder();", p1);
            Assert.Contains("int n = 41;", p1);
            Assert.Contains("string s = \"ab\";", p1);
            Assert.Contains("foreach (string c in new[]", p1);

            // The precompiled metadata-boundary dll ships into the intermediate
            // tree (HintPath resolution: source-tree fallback).
            Assert.True(File.Exists(Path.Combine(
                destDir, IntermediateDirName, "SampleDynamicDesugarLib", "external",
                "SampleDynamicBoundary.dll")));
        }
        finally
        {
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_Strict_RegisteredResidualsEscalateToError_AndFail()
    {
        // Operate on a temp copy so the fixture's shipped manifest is never
        // touched; the copied manifest is auto-discovered at the source root.
        var sourceDir = TempDir("cs2j-dd-strict-src-");
        var destDir = TempDir("cs2j-dd-strict-out-");
        try
        {
            CopyDirectory(FixtureDir, sourceDir);

            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "dynamic-desugar",
                "--dynamic-mode", "Rewrite", "--dynamic-strict",
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            Assert.NotEqual(0, exit);
            var errorCount = System.Text.RegularExpressions.Regex.Count(output, "error DS060");
            Assert.Equal(28, errorCount);
            Assert.DoesNotContain("DS030", output);
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        }
    }

    [Fact]
    public async Task Rewrite_UnregisteredResidual_FailsWithDS030()
    {
        var sourceDir = TempDir("cs2j-dd-neg-src-");
        var destDir = TempDir("cs2j-dd-neg-out-");
        var manifestPath = Path.Combine(Path.GetTempPath(),
            "cs2j-dd-empty-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Directory.CreateDirectory(sourceDir);
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Lib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>disable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            // dynamic parameter/return signatures are never concretized: both
            // sites survive Rewrite and must be caught by the residual gate.
            await File.WriteAllTextAsync(Path.Combine(sourceDir, "Class1.cs"), """
                namespace DynamicSample;

                public class Box
                {
                    public static dynamic Echo(dynamic value) => value;
                }
                """);
            await File.WriteAllTextAsync(manifestPath,
                "{\"comment\":\"empty for DS030 negative\",\"entries\":[]}");

            var (exit, output) = await RunCliAsync(
                "preprocess", "--pass", "dynamic-desugar",
                "--dynamic-mode", "Rewrite",
                "--dynamic-audit-manifest", manifestPath,
                "--no-verify-build", "--no-verify-tests",
                "-s", sourceDir, "-d", destDir);

            Assert.NotEqual(0, exit);
            Assert.Contains("DS030", output);
            Assert.Contains("DynamicReturn", output);
            Assert.Contains("DynamicParameter", output);
        }
        finally
        {
            if (Directory.Exists(sourceDir)) Directory.Delete(sourceDir, recursive: true);
            if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
        }
    }

    // DS061 (stale manifest entry) is covered at Core level in
    // DynamicDesugarUnitTests; the CLI adds no independent branching beyond the
    // gate it already exercises through the DS030/DS060 cases above.
}
