using System.Text.Json;
using CSharpToJava.Core.VarianceAudit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using VarianceKind = CSharpToJava.Core.VarianceAudit.VarianceKind;

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
        MetadataReference.CreateFromFile(typeof(System.Linq.Expressions.Expression).Assembly.Location),
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

    // ---- batch 2 (M1b): declaration/use enumeration, position flow, D7/D12 ----

    private static (VarianceAuditPreprocessor Preprocessor, VarianceAuditAnalysis Analysis, CSharpCompilation Compilation)
        Analyze(string source)
    {
        var compilation = Compile(source);
        var preprocessor = new VarianceAuditPreprocessor();
        preprocessor.BindCompilation(compilation);
        return (preprocessor, preprocessor.Analyze(), compilation);
    }

    private static VarianceFileAudit AuditOf(VarianceAuditPreprocessor preprocessor, CSharpCompilation compilation)
        => preprocessor.AuditFile(compilation.GetSemanticModel(compilation.SyntaxTrees.Single()));

    private static int CountCode(VarianceFileAudit audit, string code)
        => audit.Diagnostics.Count(d => d.Code == code);

    [Fact]
    public void Producer_interface_records_declaration_and_producer_use()
    {
        var (preprocessor, analysis, compilation) = Analyze("interface IProducer<out T> { T Produce(); }");

        var declaration = Assert.Single(analysis.DeclarationSites);
        Assert.Equal(VarianceContainer.Interface, declaration.Container);
        Assert.Equal(VarianceKind.Out, declaration.Kind);
        Assert.Contains("IProducer", declaration.TypeName);
        Assert.Equal("T", declaration.TypeParameterName);
        Assert.False(declaration.BaselineHasErrors);

        var use = Assert.Single(analysis.UseSites);
        Assert.Equal(UseFlow.Producer, use.Flow);
        Assert.Equal("? extends T", use.SuggestedWildcard);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.DeclarationSite));
        Assert.Equal(1, CountCode(audit, VarianceCodes.ProducerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ConsumerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.NestedCompositionSite));
        Assert.DoesNotContain(audit.Diagnostics,
            d => d.Severity is VarianceSeverity.Warning or VarianceSeverity.Error);
    }

    [Fact]
    public void Consumer_interface_records_declaration_and_consumer_use()
    {
        var (preprocessor, analysis, compilation) = Analyze("interface IConsumer<in T> { void Consume(T x); }");

        var declaration = Assert.Single(analysis.DeclarationSites);
        Assert.Equal(VarianceContainer.Interface, declaration.Container);
        Assert.Equal(VarianceKind.In, declaration.Kind);

        var use = Assert.Single(analysis.UseSites);
        Assert.Equal(UseFlow.Consumer, use.Flow);
        Assert.Equal("? super T", use.SuggestedWildcard);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.DeclarationSite));
        Assert.Equal(1, CountCode(audit, VarianceCodes.ConsumerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ProducerUseSite));
    }

    [Fact]
    public void Nested_composition_forward_composes_to_producer()
    {
        const string source =
            "using System; using System.Collections.Generic; "
            + "interface IX<out T> { void M(IEnumerable<Action<T>> x); }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Single(analysis.DeclarationSites);
        var use = Assert.Single(analysis.UseSites);
        Assert.True(use.Tags.HasFlag(UseTag.NestedComposition));
        Assert.True(use.CompositionChain.Count >= 2);
        Assert.Equal(UseFlow.Producer, use.Flow);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.NestedCompositionSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ProducerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ConsumerUseSite));
    }

    [Fact]
    public void Nested_composition_reverse_composes_to_consumer()
    {
        const string source =
            "interface IFoo<out T> { T Get(); } "
            + "interface IBar<in T> { void Put(T x); } "
            + "interface IX<in T> { IFoo<IBar<T>> M(); }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Equal(3, analysis.DeclarationSites.Count);
        var nested = Assert.Single(analysis.UseSites, use => use.Tags.HasFlag(UseTag.NestedComposition));
        Assert.True(nested.CompositionChain.Count >= 2);
        Assert.Equal(UseFlow.Consumer, nested.Flow);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.NestedCompositionSite));
    }

    [Fact]
    public void Delegate_declaration_records_both_variant_parameters()
    {
        var (preprocessor, analysis, compilation) = Analyze("delegate TResult Conv<in TIn, out TOut>(TIn x);");

        Assert.Equal(2, analysis.DeclarationSites.Count);
        Assert.All(analysis.DeclarationSites, declaration => Assert.Equal(VarianceContainer.Delegate, declaration.Container));
        Assert.Contains(analysis.DeclarationSites, declaration => declaration.Kind == VarianceKind.In);
        Assert.Contains(analysis.DeclarationSites, declaration => declaration.Kind == VarianceKind.Out);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(2, CountCode(audit, VarianceCodes.DeclarationSite));
    }

    [Fact]
    public void Baseline_error_tree_suppresses_safety_diagnostics()
    {
        const string source =
            "class C<out T> { } "
            + "interface IProducer<out T> { T Produce(); } "
            + "class D { IProducer<string> Get() => null; }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.NotEmpty(analysis.UseSites);
        Assert.All(analysis.DeclarationSites, declaration => Assert.True(declaration.BaselineHasErrors));
        Assert.All(analysis.UseSites, use => Assert.True(use.BaselineHasErrors));

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.DeclarationSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ProducerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ConsumerUseSite));
        Assert.Equal(0, CountCode(audit, VarianceCodes.NestedCompositionSite));
    }

    [Fact]
    public void Report_json_serializes_sites_sorted_and_byte_stable()
    {
        const string source =
            "interface IProducer<out T> { T Produce(); } "
            + "class D { IProducer<string> Get() => null; }";
        var (preprocessor, _, _) = Analyze(source);

        var first = preprocessor.BuildReportJson();
        var second = preprocessor.BuildReportJson();
        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;

        var declarations = root.GetProperty("declarationSites").EnumerateArray().ToList();
        Assert.Single(declarations);
        Assert.Equal("Test.cs", declarations[0].GetProperty("filePath").GetString());
        Assert.True(declarations[0].GetProperty("baselineHasErrors").GetBoolean() == false);

        var uses = root.GetProperty("useSites").EnumerateArray().ToList();
        Assert.Equal(2, uses.Count);
        Assert.True(uses[0].GetProperty("line").GetInt32() <= uses[1].GetProperty("line").GetInt32());

        var firstUse = uses[0];
        Assert.True(firstUse.TryGetProperty("compositionChain", out _));
        Assert.True(firstUse.TryGetProperty("tags", out _));
        Assert.True(firstUse.TryGetProperty("enclosedByExpressionTree", out _));
        Assert.True(firstUse.TryGetProperty("enclosedByDynamic", out _));
        Assert.False(firstUse.GetProperty("varianceConversion").GetBoolean());
        Assert.NotEmpty(firstUse.GetProperty("suggestedWildcard").GetString()!);
        Assert.NotEqual(0, firstUse.GetProperty("typeArguments").GetArrayLength());

        var totals = root.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("useSitesTotal").GetInt32());
        Assert.Equal(1, totals.GetProperty("variantDeclarationsOutInterface").GetInt32());
    }

    [Fact]
    public void Expression_tree_use_site_is_marked_without_errors()
    {
        const string source =
            "using System; using System.Linq.Expressions; "
            + "interface IProducer<out T> { T Produce(); } "
            + "class C { void M() { Expression<Func<IProducer<string>, string>> e = "
            + "(IProducer<string> p) => p.Produce(); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var enclosed = analysis.UseSites.Single(use => use.EnclosedByExpressionTree);
        Assert.Equal(UseFlow.Consumer, enclosed.Flow);

        var audit = AuditOf(preprocessor, compilation);
        Assert.DoesNotContain(audit.Diagnostics,
            d => d.Severity is VarianceSeverity.Warning or VarianceSeverity.Error);
    }
}