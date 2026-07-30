using CSharpToJava.Core.ReadOnlyStructMaker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests;

public partial class ReadOnlyStructMakerTests
{
    private static ReadOnlyStructMakerResult RunMaker(string source, ReadOnlyStructMakerOptions? options = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new Core.ReadOnlyStructMaker.ReadOnlyStructMaker()
            .MakeReadOnly(tree, compilation.GetSemanticModel(tree), options);
    }

    [Fact]
    public void Options_Defaults()
    {
        var o = new ReadOnlyStructMakerOptions();
        Assert.Equal("DoNotMakeReadOnly", o.OptOutAttributeName);
        Assert.True(o.ReportSkipped);
        Assert.False(o.Strict);
        Assert.True(o.EnableMethodMigration);
        Assert.True(o.EnableDtoConversion);
        Assert.True(o.UpdateCallSites);
        Assert.Null(o.AllowedLevels);
    }

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

    // === L0 Skip Tests ===

    [Fact]
    public void AlreadyReadonlyStruct_IsSkipped()
    {
        var src = "readonly struct S { public readonly int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void RefStruct_IsSkipped()
    {
        var src = "ref struct S { public int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void PartialStruct_IsSkipped()
    {
        var src = "partial struct S { public int X; }";
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }

    [Fact]
    public void DoNotMakeReadOnlyAttribute_IsSkipped()
    {
        var src = """
        [System.AttributeUsage(System.AttributeTargets.Struct)]
        class DoNotMakeReadOnlyAttribute : System.Attribute { }
        [DoNotMakeReadOnly]
        struct S { public int X; }
        """;
        var result = RunMaker(src);
        Assert.False(result.Changed);
        Assert.Contains(result.Diagnostics, d => d.Level == ConversionLevel.Skip);
    }
}
