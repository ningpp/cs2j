using System.Text.Json;
using CSharpToJava.CLI.Preprocessing;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M0: per-run preprocess-report schema/serialization contract
/// (camelCase, chain pass order, failed entries, empty diagnostics,
/// UTF-8 without BOM).
/// </summary>
public sealed class PreprocessReportWriterTests
{
    [Fact]
    public void Write_serializes_every_pass_in_chain_order()
    {
        var directory = CreateTempDirectory("cs2j-preprocess-report");
        var path = Path.Combine(directory, PreprocessReportWriter.DefaultFileName);

        try
        {
            var report = new PreprocessReport(
                SchemaVersion: 1,
                Profile: "default",
                Source: @"C:\src\App.sln",
                Destination: @"C:\out",
                Success: true,
                TotalSeconds: 123.4,
                Passes: new[]
                {
                    new PassReportEntry(
                        Id: "conditional",
                        Status: "ok",
                        TotalSeconds: 3.92,
                        Stages: new Dictionary<string, double>
                        {
                            ["prepare"] = 1.15,
                            ["execute"] = 2.77,
                        },
                        Summary: "strip-conditional: 3 branch(es) removed",
                        Diagnostics: new[] { "[Warning] CS2J0001: #warning kept" },
                        IntermediateDirectory: ".cs2j-conditional-src"),
                    new PassReportEntry(
                        Id: "normalization-audit",
                        Status: "ok",
                        TotalSeconds: 2.1,
                        Stages: new Dictionary<string, double>(),
                        Summary: string.Empty,
                        Diagnostics: Array.Empty<string>(),
                        IntermediateDirectory: ".cs2j-normalization-audit-src"),
                });

            PreprocessReportWriter.Write(path, report);

            Assert.False(FileStartsWithBom(path));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("default", root.GetProperty("profile").GetString());
            Assert.Equal(@"C:\src\App.sln", root.GetProperty("source").GetString());
            Assert.Equal(@"C:\out", root.GetProperty("destination").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(123.4, root.GetProperty("totalSeconds").GetDouble());

            var passes = root.GetProperty("passes").EnumerateArray().ToList();
            Assert.Equal(2, passes.Count);
            Assert.Equal("conditional", passes[0].GetProperty("id").GetString());
            Assert.Equal("ok", passes[0].GetProperty("status").GetString());
            Assert.Equal(3.92, passes[0].GetProperty("totalSeconds").GetDouble());
            Assert.Equal(
                "strip-conditional: 3 branch(es) removed",
                passes[0].GetProperty("summary").GetString());
            Assert.Equal(
                new[] { "[Warning] CS2J0001: #warning kept" },
                passes[0].GetProperty("diagnostics")
                    .EnumerateArray()
                    .Select(value => value.GetString())
                    .ToArray());
            Assert.Equal(
                ".cs2j-conditional-src",
                passes[0].GetProperty("intermediateDirectory").GetString());

            var stages = passes[0].GetProperty("stages");
            Assert.Equal(1.15, stages.GetProperty("prepare").GetDouble());
            Assert.Equal(2.77, stages.GetProperty("execute").GetDouble());

            Assert.Equal("normalization-audit", passes[1].GetProperty("id").GetString());
            Assert.Equal(string.Empty, passes[1].GetProperty("summary").GetString());
            Assert.Empty(passes[1].GetProperty("diagnostics").EnumerateArray());
            Assert.Empty(passes[1].GetProperty("stages").EnumerateObject());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_records_failed_pass_and_no_passes_after_it()
    {
        var directory = CreateTempDirectory("cs2j-preprocess-report-failed");
        var path = Path.Combine(directory, PreprocessReportWriter.DefaultFileName);

        try
        {
            var report = new PreprocessReport(
                SchemaVersion: 1,
                Profile: "java-ready",
                Source: "/src/App",
                Destination: "/out",
                Success: false,
                TotalSeconds: 9.5,
                Passes: new[]
                {
                    new PassReportEntry(
                        Id: "conditional",
                        Status: "ok",
                        TotalSeconds: 1.0,
                        Stages: new Dictionary<string, double> { ["prepare"] = 0.4, ["execute"] = 0.6 },
                        Summary: "conditional: ok",
                        Diagnostics: Array.Empty<string>(),
                        IntermediateDirectory: ".cs2j-conditional-src"),
                    new PassReportEntry(
                        Id: "java-keywords",
                        Status: "failed",
                        TotalSeconds: 2.0,
                        Stages: new Dictionary<string, double>(),
                        Summary: string.Empty,
                        Diagnostics: new[] { "[Error] java-keywords: rewrite failed" },
                        IntermediateDirectory: ".cs2j-java-keywords-src"),
                });

            PreprocessReportWriter.Write(path, report);

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal("java-ready", root.GetProperty("profile").GetString());

            var passes = root.GetProperty("passes").EnumerateArray().ToList();
            Assert.Equal(2, passes.Count);
            Assert.Equal("conditional", passes[0].GetProperty("id").GetString());
            Assert.Equal("ok", passes[0].GetProperty("status").GetString());
            Assert.Equal("java-keywords", passes[1].GetProperty("id").GetString());
            Assert.Equal("failed", passes[1].GetProperty("status").GetString());
            Assert.Equal(
                "[Error] java-keywords: rewrite failed",
                passes[1].GetProperty("diagnostics")[0].GetString());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void Write_emits_empty_passes_array_for_an_unexecuted_chain()
    {
        var directory = CreateTempDirectory("cs2j-preprocess-report-empty");
        var path = Path.Combine(directory, PreprocessReportWriter.DefaultFileName);

        try
        {
            PreprocessReportWriter.Write(
                path,
                new PreprocessReport(
                    SchemaVersion: 1,
                    Profile: "audit",
                    Source: "/src/App",
                    Destination: "/out",
                    Success: false,
                    TotalSeconds: 0.2,
                    Passes: Array.Empty<PassReportEntry>()));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Empty(root.GetProperty("passes").EnumerateArray());
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void ResolvePath_keeps_the_default_destination_report()
    {
        var destination = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cs2j-report-path-default"));

        Assert.Equal(
            Path.Combine(destination, PreprocessReportWriter.DefaultFileName),
            PreprocessReportWriter.ResolvePath(destination, null));
        Assert.Equal(
            Path.Combine(destination, PreprocessReportWriter.DefaultFileName),
            PreprocessReportWriter.ResolvePath(destination, string.Empty));
    }

    [Fact]
    public void ResolvePath_resolves_relative_report_against_the_destination()
    {
        var destination = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cs2j-report-path-relative"));

        Assert.Equal(
            Path.Combine(destination, "custom-report.json"),
            PreprocessReportWriter.ResolvePath(destination, "custom-report.json"));
        Assert.Equal(
            Path.Combine(destination, "custom", "report.json"),
            PreprocessReportWriter.ResolvePath(destination, Path.Combine("custom", "report.json")));
    }

    [Fact]
    public void ResolvePath_uses_an_absolute_report_path_as_is()
    {
        var destination = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cs2j-report-path-absolute-dest"));
        var absoluteReport = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "cs2j-report-path-absolute", "report.json"));

        Assert.Equal(
            absoluteReport,
            PreprocessReportWriter.ResolvePath(destination, absoluteReport));
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
