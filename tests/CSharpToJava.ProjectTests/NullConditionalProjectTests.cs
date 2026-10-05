using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C35 (null-conditional read-side desugaring),
/// driven through the preprocess CLI like <see cref="InterpolationProjectTests"/>:
/// <list type="bullet">
/// <item>positive fixture with --null-conditional: the intermediate tree
/// carries no residual ?./?[] nodes, contains lifted __ed_s temps, and the
/// intermediate tree builds AND passes its xUnit tests;</item>
/// <item>red line without the switch: the intermediate tree is
/// byte-for-byte identical to the fixture, yet still builds and tests pass.</item>
/// </list>
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class NullConditionalProjectTests
{
    private const string PassId = "expression-desugar";
    private const string IntermediateDirName = ".cs2j-expression-desugar-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleNullConditionalSolution");

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

    private static string NewOutputRoot(string tag)
        => Path.Combine(
            Path.GetTempPath(),
            "cs2j-c35-" + tag + "-" + Guid.NewGuid().ToString("N"));

    /// <summary>*.cs files carrying residual ?./?[] syntax (bin/obj and Cs2j* trees excluded).</summary>
    private static List<string> FilesWithNullConditionalSyntax(string intermediateRoot)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     intermediateRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(intermediateRoot, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p =>
                    p.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("Cs2j", StringComparison.Ordinal)))
            {
                continue;
            }

            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            var count = root.DescendantNodes().Count(n =>
                n is ConditionalAccessExpressionSyntax
                    or MemberBindingExpressionSyntax
                    or ElementBindingExpressionSyntax);
            if (count > 0)
                hits.Add(relative + "|" + count);
        }

        return hits;
    }

    [Fact]
    public async Task Enabled_LowersSites_IntermediateBuildsAndTestsPass()
    {
        var outputRoot = NewOutputRoot("on");
        try
        {
            var sln = Path.Combine(FixtureDir, "SampleNullConditionalSolution.sln");
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                "--null-conditional",
                "-s", sln,
                "-d", outputRoot);

            Assert.True(exit == 0, "expected exit 0 (incl. build/test verify); output:\n" + output);

            // The C35 summary segment only exists in enabled runs.
            Assert.Contains("nc:", output);

            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            Assert.True(
                Directory.Exists(intermediate),
                "missing intermediate tree: " + intermediate);

            var residualFiles = FilesWithNullConditionalSyntax(intermediate);
            Assert.True(
                residualFiles.Count == 0,
                "residual null-conditional nodes: " + string.Join("; ", residualFiles));

            var sites = await File.ReadAllTextAsync(Path.Combine(
                intermediate, "SampleNullConditionalLib", "Sites.cs"));
            Assert.Contains("__ed_s", sites);

            // C51: the assignment fixture projects are mirrored and fully
            // lowered; the CLI verify step already built and ran their xUnit
            // runtime tests (7) against this intermediate tree.
            var assignmentSitesPath = Path.Combine(
                intermediate,
                "NullConditionalAssignment", "AssignmentLib", "Sites.cs");
            Assert.True(
                File.Exists(assignmentSitesPath),
                "missing mirrored assignment fixture: " + assignmentSitesPath);
            var assignmentSites = await File.ReadAllTextAsync(assignmentSitesPath);

            // Whitelist receiver (parameter) inlines as a guarded if with no
            // receiver temp; coalesce sites carry the Q4 inner temp.
            Assert.Contains("if (n != null)", assignmentSites);
            Assert.Contains("__ed_n", assignmentSites);

            // E03 four-quadrant: assignment-terminal conditional chains are
            // gone (no ?./?[ and no ??= on a conditional target), while the
            // plain ??= site is left for the upstream null-coalesce pass /
            // native compiler. Residual detection is syntax-based rather than
            // a raw text scan: the fixture's own doc comment mentions the
            // "?." token in prose (<c>?.</c>), which must not count as a site.
            var assignmentRoot = CSharpSyntaxTree.ParseText(assignmentSites)
                .GetCompilationUnitRoot();
            var residualNullConditionals = assignmentRoot.DescendantNodes().Count(n =>
                n is ConditionalAccessExpressionSyntax
                    or MemberBindingExpressionSyntax
                    or ElementBindingExpressionSyntax);
            Assert.True(
                residualNullConditionals == 0,
                "residual null-conditional nodes in AssignmentLib/Sites.cs: "
                    + residualNullConditionals);
            Assert.Contains("n.Label ??= v", assignmentSites);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_RedLine_ByteIdentical_AndStillVerifies()
    {
        var outputRoot = NewOutputRoot("off");
        try
        {
            var sln = Path.Combine(FixtureDir, "SampleNullConditionalSolution.sln");
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                // C24 initializer lowering is default-on and would change the
                // fixture on its own; --no-initializer-desugar isolates the
                // C35 red line (byte-identical solely wrt --null-conditional).
                "--no-initializer-desugar",
                "-s", sln,
                "-d", outputRoot);

            Assert.True(exit == 0, "expected exit 0 (incl. build/test verify); output:\n" + output);

            Assert.DoesNotContain("nc:", output);

            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            Assert.True(Directory.Exists(intermediate));

            // Every fixture .cs file lands byte-identical in the tree.
            foreach (var sourceFile in Directory.EnumerateFiles(
                         FixtureDir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(FixtureDir, sourceFile);
                var intermediateFile = Path.Combine(intermediate, relative);
                Assert.True(
                    File.Exists(intermediateFile),
                    "missing intermediate copy: " + relative);
                Assert.Equal(
                    await File.ReadAllBytesAsync(sourceFile),
                    await File.ReadAllBytesAsync(intermediateFile));
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }
}
