using CSharpToJava.Core.ReadOnlyStructMaker;

namespace CSharpToJava.Tests;

public partial class ReadOnlyStructMakerTests
{
    [Fact]
    public void Diagnostics_Record_RoundTrip()
    {
        var d = new ReadOnlyStructMakerDiagnostic(
            ReadOnlyStructSeverity.Info, "Point", "converted", ConversionLevel.DirectAdd,
            StructPattern.FullImmutable, "Point.cs", 12);
        Assert.Equal(ReadOnlyStructSeverity.Info, d.Severity);
        Assert.Equal("Point", d.StructName);
        Assert.Equal("converted", d.Reason);
        Assert.Equal(ConversionLevel.DirectAdd, d.Level);
        Assert.Equal(StructPattern.FullImmutable, d.Pattern);
        Assert.Equal("Point.cs", d.FilePath);
        Assert.Equal(12, d.Line);
    }

    [Fact]
    public void Statistics_Starts_Zero()
    {
        var s = new ReadOnlyStructMakerStatistics();
        Assert.Equal(0, s.StructsScanned);
        Assert.Equal(0, s.StructsConverted);
        Assert.Equal(0, s.StructsSkipped);
        Assert.Equal(0, s.StructsFailed);
        Assert.Equal(0, s.Level0_Skipped);
        Assert.Equal(0, s.Level1_DirectAdd);
        Assert.Equal(0, s.Level2_PropertyConvert);
        Assert.Equal(0, s.Level3_DataContainer);
        Assert.Equal(0, s.Level5_MethodMigrate);
        Assert.Equal(0, s.MethodsMigrated);
        Assert.Equal(0, s.CallSitesUpdated);
    }

    [Fact]
    public void Result_Record_Properties()
    {
        var stats = new ReadOnlyStructMakerStatistics();
        var diags = new List<ReadOnlyStructMakerDiagnostic>();
        var files = new Dictionary<string, string>();
        var result = new ReadOnlyStructMakerResult("code", true, files, diags, stats);
        Assert.Equal("code", result.OutputCode);
        Assert.True(result.Changed);
        Assert.Same(files, result.ChangedFiles);
        Assert.Same(diags, result.Diagnostics);
        Assert.Same(stats, result.Statistics);
    }
}
