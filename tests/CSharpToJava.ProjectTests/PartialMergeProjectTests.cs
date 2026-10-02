using System.Text;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.PartialMerge;

namespace CSharpToJava.ProjectTests;

/// <summary>
/// C20 project-level acceptance (design §7.2/§7.3): direct adapter + runner.
/// run.Success proves the intermediate tree passes dotnet build + dotnet test
/// (BehaviorTests are the pre/post behavior-equivalence gate) and that the
/// residual gate emitted no CS2J2018.
/// </summary>
[Collection("SamplePartialSolution")]
public sealed class PartialMergeProjectTests
{
    private static string FixtureDir
        => Path.Combine(AppContext.BaseDirectory, "SamplePartialSolution");

    /// <summary>
    /// Scratch root lives under the test bin directory rather than %TEMP%: this
    /// machine runs a janitor that renames temp directories created by short-lived
    /// child processes to *.zombie-{pid} and deletes them; the verify dotnet
    /// children would race our finally there.
    /// </summary>
    private static string NewScratchRoot(string tag)
        => Path.Combine(AppContext.BaseDirectory, ".tmp-c20", tag + "-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Pass_Succeeds_IntermediateTree_BuildAndTest_Residual_Zero()
    {
        var outputRoot = NewScratchRoot("out");
        try
        {
            var adapter = new PartialMergePassAdapter(
                new PartialMergeOptions(),
                verbose: false,
                filterUnsupportedProjects: false,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: true);

            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(FixtureDir, "SamplePartialSolution.slnx"),
                DestinationRoot = outputRoot,
            });

            Assert.True(
                run.Success,
                string.Join("\n", adapter.BuildChainDiagnosticLines(verbose: true)));

            var intermediate = Directory.GetFiles(
                run.IntermediateRoot, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(intermediate);

            // A non-primary part leaves a provenance comment. A file carrying that
            // comment without the merge banner (i.e. a part-only file) must not
            // keep any partial declaration; same-file doubles legitimately keep
            // both the comment and the merged declaration (banner present).
            Assert.DoesNotContain(intermediate, file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains("cs2j-partial-merge: part of", StringComparison.Ordinal)
                    && !text.Contains("cs2j-partial-merge: merged from", StringComparison.Ordinal)
                    && text.Contains("public partial", StringComparison.Ordinal);
            });

            // Residual gate emitted nothing.
            Assert.DoesNotContain(
                adapter.BuildChainDiagnosticLines(verbose: true),
                line => line.Contains(PartialMergeCodes.Residual, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Disabled_Path_Is_Byte_Identical_To_Input()
    {
        // A tree without partial groups exercises the same 1:1 input copy the
        // real run replays from; every file must survive byte-identically,
        // including the BOM (design §1.4-3: pass-off inputs are never touched).
        var srcDir = NewScratchRoot("clean");
        var projectDir = Path.Combine(srcDir, "CleanProj");
        Directory.CreateDirectory(projectDir);
        var outputDir = Path.Combine(srcDir, "out");
        try
        {
            File.WriteAllText(Path.Combine(projectDir, "CleanProj.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projectDir, "NoProps.cs"),
                """
                namespace CleanProj;

                public class NoProps
                {
                    private int _v;

                    public int V() => _v;

                    public void Set(int value) => _v = value;
                }
                """);
            File.WriteAllText(Path.Combine(projectDir, "WithBom.cs"),
                """
                namespace CleanProj;

                public static class WithBom
                {
                    public static int Id() => 1;
                }
                """, new UTF8Encoding(true));

            var adapter = new PartialMergePassAdapter(
                new PartialMergeOptions(),
                verbose: false,
                filterUnsupportedProjects: false,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: false);
            var run = await ProjectPassRunner.RunAsync(adapter, new ProjectPassRunRequest
            {
                SourcePath = Path.Combine(projectDir, "CleanProj.csproj"),
                DestinationRoot = outputDir,
                Force = true,
            });

            Assert.True(
                run.Success,
                string.Join("\n", adapter.BuildChainDiagnosticLines(verbose: true)));

            foreach (var file in Directory.GetFiles(
                         run.IntermediateRoot, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(run.IntermediateRoot, file);
                if (Path.GetFileName(relative) is "Directory.Build.targets" or "Directory.Build.props")
                {
                    continue; // injected during tree preparation
                }

                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Contains("obj") || parts.Contains("bin"))
                {
                    continue; // restore/verify outputs
                }

                var source = Path.Combine(projectDir, relative);
                Assert.True(File.Exists(source), $"unexpected intermediate file: {relative}");
                Assert.True(
                    File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(source)),
                    $"file was modified on a clean tree: {relative}");
            }

            var bomFile = Path.Combine(run.IntermediateRoot, "WithBom.cs");
            using var stream = File.OpenRead(bomFile);
            Span<byte> prefix = stackalloc byte[3];
            var read = stream.Read(prefix);
            Assert.True(
                read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF,
                "BOM must survive the byte copy");
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }
}
