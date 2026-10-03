using System.Text.Json;
using CSharpToJava.Core.VarianceAudit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// Core unit tests for the C33 variance-audit skeleton (M1a): options defaults,
/// the no-op audit over a minimal compilation and the byte-stable report shape.
/// </summary>
public sealed class VarianceAuditUnitTests
{
    private static readonly MetadataReference[] References =
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
    };

    private static CSharpCompilation Compile(string source, string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: fileName);
        return CSharpCompilation.Create(
            "variance-audit-test",
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    [Fact]
    public void Options_defaults_are_audit_only()
    {
        var options = new VarianceAuditOptions();

        Assert.False(options.Strict);
        Assert.True(options.ReportInfo);
        Assert.True(options.CheckMappedMutability);
        Assert.False(options.ChainHasGeneric);
        Assert.False(options.ChainHasGenericDeepen);
    }

    [Fact]
    public void Minimal_project_produces_no_diagnostics()
    {
        var compilation = Compile("class C { }");
        var preprocessor = new VarianceAuditPreprocessor();
        preprocessor.BindCompilation(compilation);

        var analysis = preprocessor.Analyze();
        Assert.Empty(analysis.DeclarationSites);
        Assert.Empty(analysis.UseSites);
        Assert.Empty(analysis.ConstraintSites);

        var tree = compilation.SyntaxTrees.Single();
        var audit = preprocessor.AuditFile(compilation.GetSemanticModel(tree));
        Assert.Empty(audit.Diagnostics);
    }

    [Fact]
    public void Analyze_is_cached_and_requires_bound_compilation()
    {
        var preprocessor = new VarianceAuditPreprocessor();
        Assert.Throws<InvalidOperationException>(() => preprocessor.Analyze());

        var compilation = Compile("class C { }");
        preprocessor.BindCompilation(compilation);
        Assert.Same(preprocessor.Analyze(), preprocessor.Analyze());
    }

    [Fact]
    public void BuildReportJson_is_parseable_byte_stable_and_empty()
    {
        var compilation = Compile("class C { }");
        var preprocessor = new VarianceAuditPreprocessor(new VarianceAuditOptions
        {
            ChainHasGeneric = true,
        });
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze();
        preprocessor.Statistics.ReportsWritten = 1;

        var first = preprocessor.BuildReportJson();
        var second = preprocessor.BuildReportJson();
        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var passPresent = root.GetProperty("passPresent");
        Assert.True(passPresent.GetProperty("generic").GetBoolean());
        Assert.False(passPresent.GetProperty("genericDeepen").GetBoolean());

        var options = root.GetProperty("options");
        Assert.False(options.GetProperty("strict").GetBoolean());
        Assert.True(options.GetProperty("reportInfo").GetBoolean());
        Assert.True(options.GetProperty("checkMappedMutability").GetBoolean());

        Assert.Equal(JsonValueKind.Array, root.GetProperty("declarationSites").ValueKind);
        Assert.Empty(root.GetProperty("declarationSites").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("useSites").ValueKind);
        Assert.Empty(root.GetProperty("useSites").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("constraintSites").ValueKind);
        Assert.Empty(root.GetProperty("constraintSites").EnumerateArray());

        var totals = root.GetProperty("totals");
        Assert.Equal(JsonValueKind.Object, totals.ValueKind);
        Assert.Equal(0, totals.GetProperty("useSitesTotal").GetInt32());
        Assert.Equal(1, totals.GetProperty("reportsWritten").GetInt32());
    }

    [Fact]
    public void Diagnostic_formats_msbuild_style()
    {
        var diagnostic = new VarianceAuditDiagnostic(
            VarianceSeverity.Warning, VarianceCodes.CovariantConversionSite,
            "message", "src/A.cs", 4, 9);

        Assert.Equal("src/A.cs(4,9): warning VA010: message", diagnostic.ToMsBuildString());
    }
}