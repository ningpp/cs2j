using System.Diagnostics;
using System.Text.RegularExpressions;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Context;
using CSharpToJava.Core.EnumDeclaration;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.Pipeline;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.Workspace;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// Project-level gate for C47 equality-concat: runs the DEFAULT preprocess chain
/// plus equality-concat over SampleEqualityConcatSolution (delegate equality,
/// record equality, constructed-string equality/object escape, null concat and
/// += counting), then converts the intermediate tree to Java, compiles with
/// javac against the compat target/classes and RUNS the assertion app — the
/// runtime values are the gate, not text snapshots.
/// </summary>
public class EqualityConcatProjectTests
{
    private const string PassId = "equality-concat";

    private static string FixtureDir =>
        Path.Combine(AppContext.BaseDirectory, "SampleEqualityConcatSolution");

    /// <summary>Compat compiled classes: five levels up from the test bin dir to the worktree root.</summary>
    private static string CompatClasses =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "java", "csharptojava-compat", "target", "classes"));

    private static ConversionOptions ConversionOptions => new()
    {
        TypeMappingConfigPath = Path.Combine(
            AppContext.BaseDirectory, "config", "TypeMappings.json"),
    };

    /// <summary>Default chain (every default-enabled pass) + equality-concat.</summary>
    private static Task<CliChainResult> RunDefaultChainAsync(string outputRoot, bool verify = true)
        => CliPreprocessChain.RunAsync(new CliChainRequest
        {
            SourcePath = Path.Combine(FixtureDir, "SampleEqualityConcatSolution.sln"),
            DestinationRoot = outputRoot,
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            MemberDesugarOptionsFactory = () => new MemberDesugarOptions(),
            EnumDeclarationOptionsFactory = () => new EnumDeclarationOptions(),
            EqualityConcatOptionsFactory = () =>
                new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            StructValueSemanticsOptionsFactory = () =>
                new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            EnablePassIds = new[] { PassId },
            VerifyBuild = verify,
            VerifyTests = verify,
        });

    /// <summary>Runs chain + Java conversion over all non-support projects.</summary>
    private async Task<(CliChainResult Chain, List<ConversionResult> Converted)> PrepareAsync(
        string outputRoot, bool verify = true)
    {
        // Chain diagnostics go through Console, which xUnit drops for parallel
        // assemblies; intercept both streams for the chain duration.
        var capture = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        Console.SetOut(capture);
        Console.SetError(capture);

        CliChainResult chainResult;
        try
        {
            chainResult = await RunDefaultChainAsync(outputRoot, verify);
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }

        if (!chainResult.Success)
        {
            throw new Xunit.Sdk.XunitException(
                "equality-concat chain failed. captured chain output:\n" + capture);
        }

        SolutionLoader.EnsureMSBuildRegistered();
        using var loader = new SolutionLoader();
        var workspaceProjects = await loader.TryOpenAsync(chainResult.FinalSourcePath);
        Assert.NotNull(workspaceProjects);
        Assert.NotEmpty(workspaceProjects);

        // Support libraries are symbol sources only; never converted.
        var convertProjects = workspaceProjects
            .Where(p => !Cs2jArrayProject.IsCs2jArrayProject(p.FilePath))
            .Where(p => !Cs2jPreludeProject.IsCs2jPreludeProject(p.FilePath))
            .Where(p => !Cs2jRefOutInProject.IsCs2jRefOutInProject(p.FilePath))
            .Where(p => !Cs2jUnsignedMathProject.IsCs2jUnsignedMathProject(p.FilePath))
            .ToList();
        Assert.True(convertProjects.Count == 4,
            "expected 4 user projects, got " + convertProjects.Count + ": "
            + string.Join(", ", convertProjects.Select(p => p.FilePath)));

        var pipeline = new ConversionPipeline();
        var converted = new List<ConversionResult>();
        foreach (var wp in convertProjects)
        {
            // ProjectReferences include the Cs2jPrelude/Cs2jRefOutIn support
            // project directories: partial merge needs them as symbol sources so
            // support invocations bind (and thus hit TypeMappings), while the
            // support projects themselves are never converted.
            var extraPaths = wp.ProjectReferences
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetDirectoryName)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .ToList()!;
            converted.AddRange(await pipeline.ConvertProjectWithPartialMergeAsync(
                wp.Directory, ConversionOptions,
                additionalSemanticProjectPaths: extraPaths,
                projectName: wp.Name,
                projectFilePath: wp.FilePath,
                projectReferences: wp.ProjectReferences,
                isTestProject: wp.IsTestProject));
        }

        var failed = converted.Where(c => !c.Success).ToList();
        if (failed.Count > 0)
        {
            var details = string.Join("\n", failed.SelectMany(f =>
                f.Diagnostics.Select(d => d.ToString())));
            Assert.Fail("Java conversion produced failures:\n" + details);
        }

        Assert.NotEmpty(converted);
        return (chainResult, converted);
    }

    [Fact]
    public async Task Fixture_Converts_Compiles_RunsAllRuntimeAssertions()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-equality-concat");
        var workRoot = Path.Combine(
            Path.GetTempPath(), "cs2j-c47-e2e-" + Guid.NewGuid().ToString("N"));

        try
        {
            var (chainResult, converted) = await PrepareAsync(outputRoot);

            // The equality-concat intermediate stage must be in the executed plan.
            Assert.Contains(".cs2j-equality-concat-src",
                chainResult.ExecutedIntermediateDirectoryNames);

            Assert.True(Directory.Exists(CompatClasses),
                "compat target/classes missing; build csharptojava-compat first: " + CompatClasses);

            var javaSrc = Path.Combine(workRoot, "src");
            var javaOut = Path.Combine(workRoot, "classes");
            Directory.CreateDirectory(javaSrc);
            Directory.CreateDirectory(javaOut);

            // Flatten all generated files into one javac unit; package dirs are
            // reconstructed by javac under -d.
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var javaFiles = new List<string>();
            foreach (var result in converted)
            {
                var name = Path.GetFileName(result.FileName ?? "Generated.java");
                if (!usedNames.Add(name))
                {
                    name = javaFiles.Count.ToString("D2") + "-" + name;
                }

                var path = Path.Combine(javaSrc, name);
                await File.WriteAllTextAsync(path, result.GeneratedCode);
                javaFiles.Add(path);
            }

            var javac = FindJdkTool("javac");
            var javacArgs = new List<string>
            {
                // Compat classpath artifacts are JDK 28 preview class files: javac
                // must itself run with preview on the matching release to read them.
                "--enable-preview",
                "--release", "28",
                "-encoding", "UTF-8",
                "-cp", CompatClasses,
                "-d", javaOut,
            };
            javacArgs.AddRange(javaFiles);
            var (javacExit, javacOut, javacErr) = RunProcess(javac, javacArgs, 300_000);
            Assert.True(javacExit == 0,
                "javac failed:\n" + javacOut + "\n" + javacErr);

            // Discover the class owning main (top-level Program class name is not
            // part of the fixture contract).
            string? mainClass = null;
            foreach (var result in converted)
            {
                mainClass = FindMainClass(result.GeneratedCode);
                if (mainClass != null) break;
            }

            Assert.NotNull(mainClass);

            // --enable-preview is required at runtime because compat classes were
            // compiled with --enable-preview (preview class file version).
            var java = FindJdkTool("java");
            var (javaExit, javaOutText, javaErrText) = RunProcess(java, new List<string>
            {
                "--enable-preview",
                "-Djava.awt.headless=true",
                "-cp", javaOut + Path.PathSeparator + CompatClasses,
                mainClass!,
            }, 300_000);

            Assert.Contains("ALL OK", javaOutText);
            Assert.Equal(0, javaExit);
            Assert.DoesNotContain("Exception", javaErrText);
        }
        finally
        {
            TryDelete(outputRoot);
            TryDelete(workRoot);
        }
    }

    [Fact]
    public async Task DelegateLib_HasNoBinaryOperators_ButReleasesPrelude_AndMapsToDelegateHelper()
    {
        var outputRoot = Path.Combine(FixtureDir, ".test-output-equality-concat-probe");
        try
        {
            // Source contract: no delegate +/- anywhere (the pass must fire on == alone).
            // Strip XML doc comments — the doc text itself mentions "+/-".
            var originalAll = await File.ReadAllLinesAsync(
                Path.Combine(FixtureDir, "SampleDelegateEqualityLib", "DelegateEquality.cs"));
            var originalSrc = string.Join("\n",
                originalAll.Where(l => !l.TrimStart().StartsWith("///")));
            Assert.DoesNotContain("+", originalSrc);
            Assert.DoesNotContain("-", originalSrc);

            var (chainResult, converted) = await PrepareAsync(outputRoot, verify: false);

            // Cs2jPrelude released into the intermediate tree.
            var intermediate = Path.Combine(outputRoot, ".cs2j-equality-concat-src");
            var preludeProject = Path.Combine(
                intermediate, "Cs2jPrelude", Cs2jPreludeProject.ProjectFileName);
            Assert.True(File.Exists(preludeProject),
                "Cs2jPrelude support project missing in intermediate tree");
            Assert.True(File.Exists(
                Path.Combine(intermediate, "Cs2jPrelude", "DelegateOps.cs")));

            // The delegate lib now references the prelude project.
            var libCsproj = await File.ReadAllTextAsync(
                Path.Combine(intermediate, "SampleDelegateEqualityLib",
                    "SampleDelegateEqualityLib.csproj"));
            Assert.Contains("Cs2jPrelude.csproj", libCsproj);

            // Java mapping at the converted surface.
            var delegateJava = converted.First(
                c => c.FileName != null && c.FileName.Contains("DelegateEquality"));
            Assert.Contains("DelegateHelper.delegateEquals(", delegateJava.GeneratedCode);
            Assert.Contains("import io.github.ningpp.compat.DelegateHelper;",
                delegateJava.GeneratedCode);

            // String side maps to Objects.equals; concat/join surface untouched as
            // a string operator.
            var stringJava = converted.First(
                c => c.FileName != null && c.FileName.Contains("StringTools"));
            Assert.Contains("Objects.equals(", stringJava.GeneratedCode);
            // The emitter may collapse java.util imports into a wildcard.
            Assert.True(
                stringJava.GeneratedCode.Contains("import java.util.Objects;")
                    || stringJava.GeneratedCode.Contains("import java.util.*;"),
                "Objects surface used without a java.util import");

            // Reference-identity escape stays reference identity.
            Assert.Contains("ObjectHelper.referenceEquals(", stringJava.GeneratedCode);
        }
        finally
        {
            TryDelete(outputRoot);
        }
    }

    // ---- helpers ----

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 3 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Locates a JDK tool: prefers any jdk-28* install (compat classes are built
    /// for release 28), then JAVA_HOME, then PATH.
    /// </summary>
    private static string FindJdkTool(string tool)
    {
        string exe = tool + ".exe";
        var roots = new List<string>
        {
            @"D:\Java",
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
        };
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            roots.Insert(0, javaHome);
        }

        // Pass 1: jdk-28* anywhere.
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root, "jdk-28*"))
            {
                var candidate = Path.Combine(dir, "bin", exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Pass 2: JAVA_HOME direct, then any install.
        foreach (var root in roots.Distinct())
        {
            if (!Directory.Exists(root)) continue;
            var direct = Path.Combine(root, "bin", exe);
            if (File.Exists(direct)) return direct;
            foreach (var dir in Directory.GetDirectories(root).OrderByDescending(d => d))
            {
                var candidate = Path.Combine(dir, "bin", exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Pass 3: PATH.
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        throw new Xunit.Sdk.XunitException(
            "Could not locate " + exe + "; need a JDK 28 to run C47 Java assertions.");
    }

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string executable, IReadOnlyList<string> arguments, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            throw new Xunit.Sdk.XunitException("failed to start " + executable);
        }

        // Drain stderr concurrently with stdout: sequential ReadToEnd deadlocks
        // when a child fills the stderr (~64KB) pipe buffer while we block on stdout.
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException(
                executable + " timed out after " + timeoutMs + "ms");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static string? FindMainClass(string javaSource)
    {
        var mainMatch = Regex.Match(javaSource,
            @"static\s+(?:void|int)\s+main\s*\(", RegexOptions.Compiled);
        if (!mainMatch.Success)
        {
            return null;
        }

        var packageMatch = Regex.Match(javaSource,
            @"package\s+([\w.]+)\s*;", RegexOptions.Compiled);
        var package = packageMatch.Success ? packageMatch.Groups[1].Value + "." : "";

        var classMatches = Regex.Matches(javaSource,
            @"(?:class|record|enum)\s+(\w+)", RegexOptions.Compiled);
        foreach (Match classMatch in classMatches)
        {
            if (classMatch.Index < mainMatch.Index)
            {
                return package + classMatch.Groups[1].Value;
            }
        }

        return null;
    }
}
