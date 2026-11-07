using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.Preprocessing.Jcs;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// R13 / VS-0 step 4: the final tree carries a deterministic
/// <c>preprocess-closure-report.json</c> next to the manifest, and the chain
/// report points at it.
/// </summary>
public sealed class PreprocessClosureReportWriterTests
{
    [Fact]
    public void Write_serializes_schema_constructs_unowned_finalizers_and_summary()
    {
        var directory = CreateTempDirectory("cs2j-closure-report");

        try
        {
            var result = new SemanticClosureScanResult(
                Constructs: new[]
                {
                    new SemanticClosureConstruct(
                        SemanticClosureScanner.GenericTypeofTypeParameter,
                        "JCS.GENERIC",
                        "src/A.cs",
                        12,
                        20,
                        "generic-deepen"),
                    new SemanticClosureConstruct(
                        SemanticClosureScanner.ExplicitInvoke,
                        "JCS.CALL",
                        "src/B.cs",
                        7,
                        9,
                        null),
                },
                UnownedConstructs: new[]
                {
                    new SemanticClosureResidual(
                        PassConstructIds.SemanticClosure,
                        SemanticClosureScanner.ExplicitInvoke,
                        "JCS.CALL",
                        "src/B.cs",
                        7,
                        9,
                        null,
                        "no candidate owner pass is implemented"),
                },
                Finalizers: new[]
                {
                    new SemanticFinalizerExecution(
                        "finalize-generic",
                        "JCS.GENERIC",
                        Executed: false,
                        Reason: "no ISemanticFinalizer implementation registered (bounded no-op)"),
                },
                IsClosed: false);

            var path = PreprocessClosureReportWriter.Write(directory, result);

            Assert.Equal(
                PreprocessClosureReportWriter.DefaultFileName,
                Path.GetFileName(path));
            Assert.False(FileStartsWithBom(path));

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
            Assert.DoesNotContain("timestamp", json, StringComparison.OrdinalIgnoreCase);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.False(root.GetProperty("isClosed").GetBoolean());

            var constructs = root.GetProperty("constructs").EnumerateArray().ToList();
            Assert.Equal(2, constructs.Count);
            Assert.Equal(
                "generic-typeof-type-parameter",
                constructs[0].GetProperty("id").GetString());
            Assert.Equal("JCS.GENERIC", constructs[0].GetProperty("capability").GetString());
            Assert.Equal("src/A.cs", constructs[0].GetProperty("file").GetString());
            Assert.Equal(12, constructs[0].GetProperty("line").GetInt32());
            Assert.Equal(20, constructs[0].GetProperty("column").GetInt32());
            Assert.Equal("generic-deepen", constructs[0].GetProperty("owner").GetString());
            Assert.False(constructs[1].TryGetProperty("owner", out _));

            var unowned = Assert.Single(root.GetProperty("unowned").EnumerateArray());
            Assert.Equal("NORM-SEMANTIC-CLOSURE", unowned.GetProperty("code").GetString());
            Assert.Equal(
                "explicit-invoke",
                unowned.GetProperty("construct").GetString());
            Assert.Equal("JCS.CALL", unowned.GetProperty("capability").GetString());
            Assert.Equal("src/B.cs", unowned.GetProperty("file").GetString());
            Assert.Equal(7, unowned.GetProperty("line").GetInt32());
            Assert.False(unowned.TryGetProperty("candidateOwner", out _));
            Assert.False(string.IsNullOrWhiteSpace(unowned.GetProperty("reason").GetString()));

            var finalizers = Assert.Single(root.GetProperty("finalizers").EnumerateArray());
            Assert.Equal("finalize-generic", finalizers.GetProperty("passId").GetString());
            Assert.Equal("JCS.GENERIC", finalizers.GetProperty("capability").GetString());
            Assert.False(finalizers.GetProperty("executed").GetBoolean());

            var summary = root.GetProperty("summary");
            Assert.Equal(2, summary.GetProperty("constructCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("unownedCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("finalizerCount").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_is_deterministic_for_the_same_result()
    {
        var directory = CreateTempDirectory("cs2j-closure-report-deterministic");

        try
        {
            var result = new SemanticClosureScanResult(
                Array.Empty<SemanticClosureConstruct>(),
                Array.Empty<SemanticClosureResidual>(),
                Array.Empty<SemanticFinalizerExecution>(),
                IsClosed: true);

            var first = File.ReadAllText(PreprocessClosureReportWriter.Write(directory, result));
            var second = File.ReadAllText(PreprocessClosureReportWriter.Write(directory, result));

            Assert.Equal(first, second);
            using var document = JsonDocument.Parse(first);
            Assert.True(document.RootElement.GetProperty("isClosed").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("constructs").EnumerateArray());
            Assert.Empty(document.RootElement.GetProperty("unowned").EnumerateArray());
            Assert.Empty(document.RootElement.GetProperty("finalizers").EnumerateArray());
            Assert.Equal(
                0,
                document.RootElement.GetProperty("summary").GetProperty("constructCount").GetInt32());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Cli_chain_writes_the_closure_report_and_the_report_header()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "cs2j-closure-cli-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "out");
        Directory.CreateDirectory(source);

        try
        {
            File.WriteAllText(
                Path.Combine(source, "Program.cs"),
                "class P { void M() { int[] xs = [1, 2]; } }");
            File.WriteAllText(
                Path.Combine(source, "App.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var exit = await Program.MainImpl([
                "preprocess",
                "-s", Path.Combine(source, "App.csproj"),
                "-d", destination,
                "--pass", "expression-desugar",
                "--no-verify-build",
                "--no-verify-tests",
            ]);

            Assert.Equal(0, exit);

            var finalTree = Path.Combine(destination, ".cs2j-expression-desugar-src");
            var closurePath = Path.Combine(
                finalTree, PreprocessClosureReportWriter.DefaultFileName);
            Assert.True(File.Exists(closurePath), closurePath);

            using (var closure = JsonDocument.Parse(File.ReadAllText(closurePath)))
            {
                Assert.Equal(1, closure.RootElement.GetProperty("schemaVersion").GetInt32());
                var summary = closure.RootElement.GetProperty("summary");
                Assert.True(summary.GetProperty("constructCount").GetInt32() >= 0);
                Assert.Equal(
                    closure.RootElement.GetProperty("constructs").GetArrayLength(),
                    summary.GetProperty("constructCount").GetInt32());
                Assert.Equal(
                    closure.RootElement.GetProperty("unowned").GetArrayLength(),
                    summary.GetProperty("unownedCount").GetInt32());
                Assert.Equal(
                    closure.RootElement.GetProperty("finalizers").GetArrayLength(),
                    summary.GetProperty("finalizerCount").GetInt32());
            }

            var reportPath = Path.Combine(
                destination, PreprocessReportWriter.DefaultFileName);
            Assert.True(File.Exists(reportPath), reportPath);

            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal(
                PreprocessClosureReportWriter.DefaultFileName,
                report.RootElement.GetProperty("semanticClosure").GetString());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateTempDirectory(string prefix)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static bool FileStartsWithBom(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> prefix = stackalloc byte[3];
        var read = stream.Read(prefix);
        return read >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF;
    }
}
