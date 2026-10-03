using System.Diagnostics;
using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ExtensionMethodResolve;
using CSharpToJava.Core.IteratorLowering;
using CSharpToJava.Core.MemberDesugar;
using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C19 M4 project-level closing coverage (design §9):
/// 1. build-failure rollback drills: one attributed file is replayed
///    byte-for-byte (run still succeeds, EM001 trace) vs whole-tree rollback
///    (CS2J1200, run fails, every rewritten file reverted);
/// 2. strict E2E: the 1203 Warning-only external-host fixture becomes an
///    Error-grade failure under --extension-methods-strict;
/// 3. host overrides CLI: skip keeps sugar with EM001; malformed payloads
///    exit non-zero before any rewrite;
/// 4. EM-ORDER chain position: make-readonly (E13) then iterator-lowering
///    (E14) then C19: the earlier passes consume their sugar shapes and C19
///    explicitizes the sites that remain;
/// 5. appendix ruling 6: member-desugar (property/event/indexer/operator)
///    and C19 run serially without disturbing each other's nodes.
/// </summary>
public class ExtensionMethodM4ProjectTests
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

    private static string Compact(string s)
        => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    // ---------------------------------------------------------------------
    // 1. Rollback drills
    // ---------------------------------------------------------------------

    private const string RollbackFileA = """
        namespace M4Rollback;

        public static class AExt
        {
            public static int Boost(this int x) => x + 1;
        }

        public class AUser
        {
            public int Go() => 1.Boost();
        }
        """;

    private const string RollbackFileB = """
        namespace M4Rollback;

        public static class BExt
        {
            public static int Lift(this int x) => x * 2;
        }

        public class BUser
        {
            public int Go() => 4.Lift();
        }
        """;

    [Fact]
    public async Task BrokenRewrite_AttributedToOneFile_FileRevertedByteForByte_RunSucceeds()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-m4-rb1-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(workDir, "RollbackProj");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "RollbackProj.csproj"), SdkCsproj);
        File.WriteAllText(Path.Combine(projectDir, "RollbackA.cs"), RollbackFileA);
        File.WriteAllText(Path.Combine(projectDir, "RollbackB.cs"), RollbackFileB);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = new ExtensionMethodResolvePassAdapter(
                options: new ExtensionMethodResolveOptions(),
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: false,
                forceBrokenRewrite: (InvocationExpressionSyntax _, IMethodSymbol sym)
                    => sym.ContainingType.Name == "AExt");

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "RollbackProj.csproj"),
                DestinationRoot = destination,
                Force = true,
            });

            Assert.True(run.Success, "the reverted tree must build and the pass must stay error-free");
            Assert.Equal(1, adapter.Statistics.FilesRolledBack);
            Assert.Contains(adapter.Diagnostics,
                d => d.Code == ExtensionMethodCodes.Info && d.Message.Contains("rollback")
                    && d.Message.Contains("RollbackA.cs"));

            var intermediate = Path.Combine(destination,
                ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            var outA = File.ReadAllBytes(Directory.GetFiles(intermediate, "RollbackA.cs",
                SearchOption.AllDirectories).Single());
            var outB = File.ReadAllBytes(Directory.GetFiles(intermediate, "RollbackB.cs",
                SearchOption.AllDirectories).Single());

            Assert.Equal(File.ReadAllBytes(Path.Combine(projectDir, "RollbackA.cs")), outA);
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(projectDir, "RollbackB.cs")), outB);
            Assert.Contains("global::M4Rollback.BExt.Lift(4)",
                Compact(Encoding.UTF8.GetString(outB)));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    [Fact]
    public async Task BrokenRewrite_AttributedToMultipleFiles_WholeTreeRollback_CS2J1200()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-m4-rb2-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(workDir, "RollbackProj");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "RollbackProj.csproj"), SdkCsproj);
        File.WriteAllText(Path.Combine(projectDir, "RollbackA.cs"), RollbackFileA);
        File.WriteAllText(Path.Combine(projectDir, "RollbackB.cs"), RollbackFileB);
        var destination = Path.Combine(workDir, "out");

        try
        {
            var adapter = new ExtensionMethodResolvePassAdapter(
                options: new ExtensionMethodResolveOptions(),
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: false,
                forceBrokenRewrite: (InvocationExpressionSyntax _, IMethodSymbol _) => true);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "RollbackProj.csproj"),
                DestinationRoot = destination,
                Force = true,
            });

            Assert.False(run.Success, "CS2J1200 is Error-grade; the run must fail");
            Assert.Contains(adapter.Diagnostics,
                d => d.Code == ExtensionMethodCodes.WholeTreeRollback
                     && d.Severity == ExtensionSeverity.Error);

            var intermediate = Path.Combine(destination,
                ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            foreach (var name in new[] { "RollbackA.cs", "RollbackB.cs" })
            {
                var outBytes = File.ReadAllBytes(Directory.GetFiles(intermediate, name,
                    SearchOption.AllDirectories).Single());
                Assert.Equal(File.ReadAllBytes(Path.Combine(projectDir, name)), outBytes);
            }
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ---------------------------------------------------------------------
    // 2. Strict E2E (external metadata host)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StrictFlag_Escalates1203WarningToError_ExitNonZero()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtExternalHostSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtExternalHostSolution must be copied to the test output directory");

        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-m4-strict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var repoDir = Path.Combine(workDir, "repo");
        var hostProjectDir = Path.Combine(workDir, "host-project");
        var localFeed = Path.Combine(workDir, "local-feed");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(hostProjectDir);
        CopyDirectory(fixture, repoDir);

        try
        {
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

            RunDotnet($"pack \"{Path.Combine(hostProjectDir, "SampleExtMetadataHost.csproj")}\" -c Release -o \"{localFeed}\" --nologo",
                workDir);

            var nugetConfigPath = Path.Combine(repoDir, "nuget.config");
            var nugetConfig = await File.ReadAllTextAsync(nugetConfigPath);
            await File.WriteAllTextAsync(nugetConfigPath,
                nugetConfig.Replace("./local-feed", localFeed.Replace('\\', '/')));

            var destination = Path.Combine(workDir, "out");
            var solutionPath = Path.Combine(repoDir, "SampleExtExternalHostSolution.sln");
            var (exit, _, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", destination,
                "--pass", "extension-methods", "--extension-methods-strict", "--verbose");

            Assert.NotEqual(0, exit);
            var lines1203 = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(l => l.Contains("CS2J1203", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(lines1203);
            Assert.Contains(lines1203, l => l.Contains("error", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ---------------------------------------------------------------------
    // 3. Host overrides CLI
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Overrides_SkipHost_KeepsSugarWithInfo_InvalidPayloadsFail()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "SampleExtensionMethodsSolution");
        Assert.True(Directory.Exists(fixture),
            "SampleExtensionMethodsSolution must be copied to the test output directory");

        var workDir = Path.Combine(Path.GetTempPath(),
            "cs2j-m4-overrides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var repoDir = Path.Combine(workDir, "repo");
        CopyDirectory(fixture, repoDir);

        try
        {
            var solutionPath = Path.Combine(repoDir, "SampleExtensionMethodsSolution.sln");
            var overridesPath = Path.Combine(workDir, "overrides.json");
            await File.WriteAllTextAsync(overridesPath,
                """
                {
                  "hosts": {
                    "SampleExtLib.Text.TextExtensions": "skip"
                  }
                }
                """);

            var goodDestination = Path.Combine(workDir, "out-good");
            var (exit, stdout, stderr) = await RunCliAsync(
                "preprocess", "-s", solutionPath, "-d", goodDestination,
                "--pass", "extension-methods",
                "--extension-methods-overrides", overridesPath, "--verbose");

            Assert.True(exit == 0,
                $"valid overrides must exit 0.\nstderr: {stderr}\nstdout: {Tail(stdout)}");

            var intermediate = Path.Combine(goodDestination,
                ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            var program = await File.ReadAllTextAsync(
                Directory.GetFiles(intermediate, "Program.cs", SearchOption.AllDirectories).Single());

            // Skipped host sites stay reduced sugar.
            Assert.Contains("\"hi\".Shout()", program);
            Assert.DoesNotContain("global::SampleExtLib.Text.TextExtensions", program);
            // Other in-compilation user hosts are still explicitized.
            Assert.Contains("global::SampleExtLib.Collections", program);
            // EM001 observability for the skip.
            Assert.Contains("skipped by host overrides", stderr + stdout, StringComparison.Ordinal);

            // Malformed JSON fails before rewriting.
            var badJson = Path.Combine(workDir, "bad.json");
            await File.WriteAllTextAsync(badJson, "{ not json");
            var r1 = await RunCliAsync("preprocess", "-s", solutionPath,
                "-d", Path.Combine(workDir, "out-bad1"),
                "--pass", "extension-methods", "--extension-methods-overrides", badJson);
            Assert.NotEqual(0, r1.ExitCode);

            // Unknown action.
            var badAction = Path.Combine(workDir, "badaction.json");
            await File.WriteAllTextAsync(badAction,
                """
                { "hosts": { "Some.Host": "maybe" } }
                """);
            var r2 = await RunCliAsync("preprocess", "-s", solutionPath,
                "-d", Path.Combine(workDir, "out-bad2"),
                "--pass", "extension-methods", "--extension-methods-overrides", badAction);
            Assert.NotEqual(0, r2.ExitCode);

            // Protected LINQ hosts are immutable to overrides.
            var protectedHost = Path.Combine(workDir, "protected.json");
            await File.WriteAllTextAsync(protectedHost,
                """
                { "hosts": { "System.Linq.Enumerable": "skip" } }
                """);
            var r3 = await RunCliAsync("preprocess", "-s", solutionPath,
                "-d", Path.Combine(workDir, "out-bad3"),
                "--pass", "extension-methods", "--extension-methods-overrides", protectedHost);
            Assert.NotEqual(0, r3.ExitCode);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ---------------------------------------------------------------------
    // 4. EM-ORDER: E13 make-readonly -> E14 iterator-lowering ->
    //    eliminate-goto (E14 emits goto state machines) -> C19
    // ---------------------------------------------------------------------

    [Fact]
    public async Task EmOrder_MakeReadonlyThenIteratorThenC19_AllSitesSettledAndBuildGreen()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-m4-order-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(workDir, "ChainPos");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "ChainPos.csproj"), SdkCsproj);
        File.WriteAllText(Path.Combine(projectDir, "ReadonlySite.cs"), """
            namespace ChainPos;

            public static class SExt
            {
                public static int Score(this S s) => s.V + 1;
            }

            public struct S
            {
                public int V;
                public int Total() => this.Score();
            }
            """);
        File.WriteAllText(Path.Combine(projectDir, "IteratorSite.cs"), """
            using System.Collections.Generic;

            namespace ChainPos;

            public static class IterExt
            {
                public static int FirstOr(this IEnumerable<int> e, int d)
                {
                    using var et = e.GetEnumerator();
                    return et.MoveNext() ? et.Current : d;
                }
            }

            public class It
            {
                public IEnumerable<int> Numbers()
                {
                    yield return 1;
                    yield return 2;
                }

                public int Pick() => Numbers().FirstOr(0);
            }
            """);

        var csproj = Path.Combine(projectDir, "ChainPos.csproj");
        try
        {
            // E13: make-readonly.
            var makeReadonly = new MakeReadonlyPassAdapter(
                new ReadOnlyStructMakerOptions(),
                verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null, verifyBuild: true, verifyTests: false);
            var out1 = Path.Combine(workDir, "out-e13");
            var run1 = await ProjectPassRunner.RunAsync(makeReadonly, new ProjectPassRunRequest
            {
                SourcePath = csproj,
                DestinationRoot = out1,
                Force = true,
            });
            Assert.True(run1.Success, "E13 make-readonly run must succeed");

            // E14: iterator-lowering. The lowered state machine uses goto/
            // labels and is only compilable after eliminate-goto, so build
            // verification is deferred to that run.
            var iterator = new IteratorLoweringPassAdapter(
                new IteratorLoweringOptions(Strict: false),
                verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null, verifyBuild: false, verifyTests: false,
                strict: false);
            var out2 = Path.Combine(workDir, "out-e14");
            var run2 = await ProjectPassRunner.RunAsync(iterator, new ProjectPassRunRequest
            {
                SourcePath = run1.PreprocessedSourcePath,
                DestinationRoot = out2,
                Force = true,
            });
            Assert.True(run2.Success && !run2.StubRejected, "E14 iterator-lowering run must succeed");

            // eliminate-goto: turns the E14 state machine into structured code
            // and verifies the tree builds.
            var gotoPass = new GotoPassAdapter(
                verbose: false, strict: false,
                filterUnsupportedProjects: true, compatNamespacePrefixes: null);
            var outGoto = Path.Combine(workDir, "out-goto");
            var runGoto = await ProjectPassRunner.RunAsync(gotoPass, new ProjectPassRunRequest
            {
                SourcePath = run2.PreprocessedSourcePath,
                DestinationRoot = outGoto,
                Force = true,
            });
            Assert.True(runGoto.Success, "eliminate-goto after E14 must produce a building tree");

            // C19: the user extension sites that survived all earlier passes
            // are explicitized now.
            var c19 = new ExtensionMethodResolvePassAdapter(
                options: new ExtensionMethodResolveOptions(),
                verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null, verifyBuild: true, verifyTests: false);
            var out3 = Path.Combine(workDir, "out-c19");
            var run3 = await ProjectPassRunner.RunAsync(c19, new ProjectPassRunRequest
            {
                SourcePath = runGoto.PreprocessedSourcePath,
                DestinationRoot = out3,
                Force = true,
            });
            Assert.True(run3.Success, "C19 after E13/E14 must succeed and the final tree must build");
            Assert.True(c19.Statistics.CallsitesRewritten >= 2,
                $"both Score and FirstOr sites should be explicitized, got {c19.Statistics.CallsitesRewritten}");

            var finalRoot = Path.Combine(out3,
                ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            var allText = string.Join("\n",
                Directory.GetFiles(finalRoot, "*.cs", SearchOption.AllDirectories)
                    .Select(File.ReadAllText));
            var compact = Compact(allText);
            Assert.Contains("global::ChainPos.SExt.Score(", compact);
            Assert.Contains("global::ChainPos.IterExt.FirstOr(", compact);
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ---------------------------------------------------------------------
    // 5. Appendix ruling 6: member-desugar -> C19
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MemberDesugarThenC19_PropertyEventIndexerOperatorAndExtCoexist()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs2j-m4-mix-" + Guid.NewGuid().ToString("N"));
        var projectDir = Path.Combine(workDir, "MixProj");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "MixProj.csproj"), SdkCsproj);
        File.WriteAllText(Path.Combine(projectDir, "Mix.cs"), """
            using System;

            namespace MixProj;

            public static class BagExt
            {
                public static int ExtTotal(this Bag b) => b.Use();
            }

            public class Bag
            {
                public int Count { get; set; }
                public event Action? Hit;
                public int this[int i] => i;
                public static Bag operator +(Bag a, int x) => a;

                public int Use()
                {
                    var b = new Bag();
                    b.Count = this[0] + (this + 1).Count;
                    Hit?.Invoke();
                    return b.Count;
                }
            }

            public class Consumer
            {
                public int Go() => new Bag().ExtTotal();
            }
            """);

        var csproj = Path.Combine(projectDir, "MixProj.csproj");
        try
        {
            var desugar = new MemberDesugarPassAdapter(
                new MemberDesugarOptions(),
                verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null);
            var out1 = Path.Combine(workDir, "out-desugar");
            var run1 = await ProjectPassRunner.RunAsync(desugar, new ProjectPassRunRequest
            {
                SourcePath = csproj,
                DestinationRoot = out1,
                Force = true,
            });
            Assert.True(run1.Success, "member-desugar run must succeed");

            var c19 = new ExtensionMethodResolvePassAdapter(
                options: new ExtensionMethodResolveOptions(),
                verbose: false, filterUnsupportedProjects: true,
                compatNamespacePrefixes: null, verifyBuild: true, verifyTests: false);
            var out2 = Path.Combine(workDir, "out-c19");
            var run2 = await ProjectPassRunner.RunAsync(c19, new ProjectPassRunRequest
            {
                SourcePath = run1.PreprocessedSourcePath,
                DestinationRoot = out2,
                Force = true,
            });
            Assert.True(run2.Success, "C19 after member-desugar must succeed; tree must build");
            Assert.True(c19.Statistics.CallsitesRewritten >= 1);

            var finalRoot = Path.Combine(out2,
                ExtensionMethodResolvePassAdapter.IntermediateDirectoryName);
            var mix = await File.ReadAllTextAsync(
                Directory.GetFiles(finalRoot, "Mix.cs", SearchOption.AllDirectories).Single());
            Assert.Contains("global::MixProj.BagExt.ExtTotal(", Compact(mix));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ---------------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------------

    private const string SdkCsproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Tail(string s) =>
        s.Length <= 2000 ? s : s[^2000..];

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
