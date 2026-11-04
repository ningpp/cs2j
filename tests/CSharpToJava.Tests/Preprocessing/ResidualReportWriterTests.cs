using System.Text.Json;
using CSharpToJava.Core.Normalization;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// M0-b: residual-report schema/serialization contract (camelCase, ordering,
/// UTF-8 without BOM, deterministic chain order).
/// </summary>
public sealed class ResidualReportWriterTests
{
    [Fact]
    public void Write_serializes_the_report_and_sorts_residuals_deterministically()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "cs2j-residual-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ResidualReportWriter.DefaultFileName);

        try
        {
            var report = new ResidualReport(
                SchemaVersion: 1,
                Profile: "java-ready",
                Chain: new[] { "conditional", "java-keywords", "normalization-audit" },
                Summary: new ResidualReportSummary(FilesScanned: 3, Residuals: 2, Errors: 2, Warnings: 0),
                Residuals: new[]
                {
                    new ResidualReportEntry(
                        "NORM-STACKALLOC", "src/B.cs", 10, 5, "stackalloc byte[n]",
                        "unsafe-ffm", "Fail", "explicit stackalloc array creation"),
                    new ResidualReportEntry(
                        "NORM-REF-STRUCT", "src/A.cs", 2, 1, "ref struct R { }",
                        "unsafe-ffm", "Fail", "ref struct 'R' declaration"),
                });

            ResidualReportWriter.Write(path, report);

            var json = File.ReadAllText(path);
            Assert.False(FileStartsWithBom(path));

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("java-ready", root.GetProperty("profile").GetString());
            Assert.Equal(
                new[] { "conditional", "java-keywords", "normalization-audit" },
                root.GetProperty("chain").EnumerateArray().Select(value => value.GetString()).ToArray());

            var summary = root.GetProperty("summary");
            Assert.Equal(3, summary.GetProperty("filesScanned").GetInt32());
            Assert.Equal(2, summary.GetProperty("residuals").GetInt32());
            Assert.Equal(2, summary.GetProperty("errors").GetInt32());
            Assert.Equal(0, summary.GetProperty("warnings").GetInt32());

            var residuals = root.GetProperty("residuals").EnumerateArray().ToList();
            Assert.Equal(2, residuals.Count);
            Assert.Equal("src/A.cs", residuals[0].GetProperty("file").GetString());
            Assert.Equal("NORM-REF-STRUCT", residuals[0].GetProperty("code").GetString());
            Assert.Equal(2, residuals[0].GetProperty("line").GetInt32());
            Assert.Equal(1, residuals[0].GetProperty("column").GetInt32());
            Assert.Equal("src/B.cs", residuals[1].GetProperty("file").GetString());
            Assert.Equal("NORM-STACKALLOC", residuals[1].GetProperty("code").GetString());

            var roundTripped = JsonSerializer.Deserialize<ResidualReport>(
                json,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Assert.NotNull(roundTripped);
            Assert.Equal(1, roundTripped!.SchemaVersion);
            Assert.Equal("java-ready", roundTripped.Profile);
            Assert.Equal(
                new[] { "conditional", "java-keywords", "normalization-audit" },
                roundTripped.Chain.ToArray());
            Assert.Equal(2, roundTripped.Summary.Residuals);
            Assert.Equal("NORM-REF-STRUCT", roundTripped.Residuals[0].Code);
            Assert.Equal("unsafe-ffm", roundTripped.Residuals[0].OwnerPassId);
            Assert.Equal("Fail", roundTripped.Residuals[0].Policy);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
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
