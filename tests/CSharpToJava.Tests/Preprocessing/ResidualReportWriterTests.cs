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
                Summary: new ResidualReportSummary(FilesScanned: 4, Residuals: 4, Errors: 4, Warnings: 0)
                {
                    // 故意按非 ordinal 顺序插入，验证 writer 会稳定排序字典键。
                    ByCode = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["NORM-STACKALLOC"] = 1,
                        ["NORM-REF-STRUCT"] = 1,
                        ["NORM-FIXED"] = 1,
                        ["NORM-POINTER-LOCAL"] = 1,
                    },
                    ByOwner = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        ["unsafe-ffm"] = 3,
                        ["numeric-boundary"] = 1,
                    },
                },
                Residuals: new[]
                {
                    new ResidualReportEntry(
                        "NORM-STACKALLOC", "src/B.cs", 10, 5, "stackalloc byte[n]",
                        "unsafe-ffm", "Fail", "explicit stackalloc array creation"),
                    new ResidualReportEntry(
                        "NORM-REF-STRUCT", "src/A.cs", 2, 1, "ref struct R { }",
                        "unsafe-ffm", "Fail", "ref struct 'R' declaration"),
                    new ResidualReportEntry(
                        "NORM-POINTER-LOCAL", "src/C.cs", 4, 3, "byte* local",
                        "unsafe-ffm", "Fail", "pointer local declaration"),
                    new ResidualReportEntry(
                        "NORM-FIXED", "src/D.cs", 6, 2, "fixed (byte* p = ...)",
                        "numeric-boundary", "Fail", "fixed statement"),
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
            Assert.Equal(4, summary.GetProperty("filesScanned").GetInt32());
            Assert.Equal(4, summary.GetProperty("residuals").GetInt32());
            Assert.Equal(4, summary.GetProperty("errors").GetInt32());
            Assert.Equal(0, summary.GetProperty("warnings").GetInt32());

            Assert.Equal(
                new[] { "NORM-FIXED", "NORM-POINTER-LOCAL", "NORM-REF-STRUCT", "NORM-STACKALLOC" },
                summary.GetProperty("byCode").EnumerateObject().Select(property => property.Name).ToArray());
            Assert.All(
                summary.GetProperty("byCode").EnumerateObject(),
                property => Assert.Equal(1, property.Value.GetInt32()));

            Assert.Equal(
                new[] { "numeric-boundary", "unsafe-ffm" },
                summary.GetProperty("byOwner").EnumerateObject().Select(property => property.Name).ToArray());
            Assert.Equal(
                1,
                summary.GetProperty("byOwner").GetProperty("numeric-boundary").GetInt32());
            Assert.Equal(
                3,
                summary.GetProperty("byOwner").GetProperty("unsafe-ffm").GetInt32());

            var residuals = root.GetProperty("residuals").EnumerateArray().ToList();
            Assert.Equal(4, residuals.Count);
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
            Assert.Equal(4, roundTripped.Summary.Residuals);
            Assert.Equal(4, roundTripped.Summary.ByCode.Values.Sum());
            Assert.Equal(4, roundTripped.Summary.ByOwner.Values.Sum());
            Assert.Equal(3, roundTripped.Summary.ByOwner["unsafe-ffm"]);
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
