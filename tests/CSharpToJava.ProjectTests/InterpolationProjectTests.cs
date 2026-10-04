using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C23 (interpolation expansion), driven through the
/// preprocess CLI like <see cref="CollectionExprProjectTests"/>:
/// <list type="bullet">
/// <item>positive fixture with --interpolation-expand: products carry
/// string.Concat/string.Format, no raw/verbatim lexicon, the only residual
/// interpolation node is the CS2J0511 FormattableString preserve zone, and
/// the intermediate tree builds AND passes its xUnit tests (native vs
/// preprocessed double run);</item>
/// <item>red line without the switch: the intermediate tree is byte-for-byte
/// identical to the fixture, yet still builds and passes tests;</item>
/// <item>custom-handler fixture: strict CS2J0510 fails the run with a
/// non-zero exit code and the offending file is byte-preserved.</item>
/// </summary>
[Collection(ConsoleChainTestCollection.Name)]
public class InterpolationProjectTests
{
    private const string PassId = "expression-desugar";
    private const string IntermediateDirName = ".cs2j-expression-desugar-src";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleInterpolationSolution");

    private static string UnsupportedFixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleInterpolationUnsupportedSolution");

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
            "cs2j-c23-" + tag + "-" + Guid.NewGuid().ToString("N"));

    private static async Task<string> ReadIntermediateFileAsync(
        string intermediateRoot, params string[] parts)
        => await File.ReadAllTextAsync(Path.Combine(intermediateRoot, Path.Combine(parts)));

    /// <summary>*.cs files carrying residual interpolation syntax (bin/obj and Cs2j* trees excluded).</summary>
    private static List<string> FilesWithInterpolationSyntax(string intermediateRoot)
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
            var count = root.DescendantNodes()
                .OfType<InterpolatedStringExpressionSyntax>()
                .Count();
            if (count > 0)
                hits.Add(relative + "|" + count);
        }

        return hits;
    }

    [Fact]
    public async Task Enabled_ExpandsSites_IntermediateBuildsAndTestsPass()
    {
        var outputRoot = NewOutputRoot("on");
        try
        {
            var sln = Path.Combine(FixtureDir, "SampleInterpolationSolution.sln");
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                "--interpolation-expand",
                "-s", sln,
                "-d", outputRoot);

            Assert.True(exit == 0, "expected exit 0 (incl. build/test verify); output:\n" + output);

            // The C23 summary segment only exists in enabled runs.
            Assert.Contains("interp:", output);

            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            Assert.True(
                Directory.Exists(intermediate),
                "missing intermediate tree: " + intermediate);

            var concat = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "ConcatSites.cs");
            Assert.Contains("string.Concat", concat);
            Assert.DoesNotContain("$\"", concat);

            var unsigned = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "UnsignedSites.cs");
            Assert.Contains("string.Concat", unsigned);
            // uint/ulong holes take x.ToString(); byte/ushort take ((uint)x).ToString().
            Assert.Contains(".ToString()", unsigned);
            Assert.DoesNotContain("$\"", unsigned);

            var format = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "FormatSites.cs");
            Assert.Contains("string.Format", format);
            Assert.Contains("{0,5}", format);
            Assert.Contains("{1,-5}", format);
            Assert.Contains("{0:X2}", format);
            Assert.Contains("{0:F2}", format);
            Assert.Contains("{0:yyyy-MM-dd}", format);
            Assert.DoesNotContain("$\"", format);

            var foldNested = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "FoldNestedRawSites.cs");
            // IE-FOLD leaves a bare regular literal.
            Assert.Contains("ab-W", foldNested);
            // Nested site expands; verbatim/raw lexicon is gone.
            Assert.Contains("string.Concat", foldNested);
            Assert.DoesNotContain("$\"", foldNested);
            Assert.DoesNotContain("\"\"\"", foldNested);
            Assert.DoesNotContain("@\"", foldNested);

            // The FormattableString site is the single preserved residual.
            var preserve = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "FormattableStringSites.cs");
            Assert.Contains("$\"y={2}\"", preserve);

            var residualFiles = FilesWithInterpolationSyntax(intermediate);
            var hit = Assert.Single(residualFiles);
            Assert.EndsWith(
                "FormattableStringSites.cs|1",
                hit.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);

            // A rule-clean file (UTF-8 BOM) is copied byte-for-byte.
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(
                    FixtureDir, "SampleInterpolationLib", "CleanFile.cs")),
                await File.ReadAllBytesAsync(Path.Combine(
                    intermediate, "SampleInterpolationLib", "CleanFile.cs")));
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
            var sln = Path.Combine(FixtureDir, "SampleInterpolationSolution.sln");
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                // Rule-level kill switch: the all-on policy defaults the rule
                // to enabled, so the red-line run disables it via the codebase
                // convention --no-xxx negation switch (bool options are
                // switches in CommandLineParser; --flag=false does not bind).
                "--no-interpolation-expand",
                "-s", sln,
                "-d", outputRoot);

            Assert.True(exit == 0, "expected exit 0 (incl. build/test verify); output:\n" + output);

            // Opt-in red line: no C23 observable surface.
            Assert.DoesNotContain("interp:", output);
            Assert.DoesNotContain("CS2J0501", output);
            Assert.DoesNotContain("CS2J0502", output);

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

            var concat = await ReadIntermediateFileAsync(
                intermediate, "SampleInterpolationLib", "ConcatSites.cs");
            // Original interpolation syntax is intact.
            Assert.Contains("$\"hi {name} #{n}\"", concat);
            Assert.DoesNotContain("string.Concat", concat);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandlerFixture_StrictError_NonZero_Reports0510_FileBytesPreserved()
    {
        var outputRoot = NewOutputRoot("handler");
        try
        {
            var sln = Path.Combine(
                UnsupportedFixtureDir, "SampleInterpolationUnsupportedSolution.sln");
            var (exit, output) = await RunCliAsync(
                "preprocess",
                "--pass", PassId,
                "--interpolation-expand",
                "-s", sln,
                "-d", outputRoot);

            Assert.NotEqual(0, exit);
            Assert.Contains("CS2J0510", output);

            var intermediate = Path.Combine(outputRoot, IntermediateDirName);
            var intermediateFile = Path.Combine(
                intermediate, "SampleInterpolationUnsupportedLib", "HandlerSites.cs");
            Assert.True(
                File.Exists(intermediateFile),
                "the prepared intermediate tree is retained on strict failure");
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(
                    UnsupportedFixtureDir,
                    "SampleInterpolationUnsupportedLib", "HandlerSites.cs")),
                await File.ReadAllBytesAsync(intermediateFile));
            var text = await File.ReadAllTextAsync(intermediateFile);
            Assert.DoesNotContain("__ed_", text);
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, recursive: true);
        }
    }
}
