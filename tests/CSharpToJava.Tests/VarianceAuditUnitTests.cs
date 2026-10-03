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

    // ---- batch 3 (M1c): safety classification, mapped mutability, constraints ----

    [Fact]
    public void Covariant_conversion_argument_reports_va010()
    {
        const string source =
            "interface IProducer<out T> { T Produce(); } "
            + "class C { void M(IProducer<object> x) { } void N(IProducer<string> s) { M(s); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var provider = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        Assert.False(VariancePositionFacts.HasBaselineErrors(provider));

        var audit = AuditOf(preprocessor, compilation);
        var diagnostic = Assert.Single(audit.Diagnostics, d => d.Code == VarianceCodes.CovariantConversionSite);
        Assert.Equal(VarianceSeverity.Warning, diagnostic.Severity);
        Assert.Contains("covariant conversion", diagnostic.Message);
        Assert.Equal(1, preprocessor.Statistics.VarianceConversionsCovariant);
        Assert.True(analysis.UseSites.Count >= 1);
    }

    [Fact]
    public void Enumerable_covariant_conversion_reports_va010()
    {
        const string source =
            "using System.Collections.Generic; "
            + "class C { void M(IEnumerable<object> x) { } void N(IEnumerable<string> s) { M(s); } }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.CovariantConversionSite));
    }

    [Fact]
    public void Contravariant_delegate_argument_reports_va011()
    {
        const string source =
            "using System; "
            + "class C { void M(Action<string> a) { } void N(Action<object> ao) { M(ao); } }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        var diagnostic = Assert.Single(audit.Diagnostics, d => d.Code == VarianceCodes.ContravariantConversionSite);
        Assert.Contains("contravariant conversion", diagnostic.Message);
        Assert.Equal(1, preprocessor.Statistics.VarianceConversionsContravariant);
    }

    [Fact]
    public void Method_group_delegate_conversion_reports_va011()
    {
        const string source =
            "using System; "
            + "class C { static void Handler(object o) { } static void M(Action<string> a) { } "
            + "static void N() { M(Handler); } }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.ContravariantConversionSite));
    }

    [Fact]
    public void Unresolved_dynamic_conversion_reports_va015()
    {
        const string source =
            "interface IProducer<out T> { T Produce(); } "
            + "class C { void M(IProducer<object> x) { } void N(dynamic d) { M(d); } }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        var diagnostic = Assert.Single(audit.Diagnostics, d => d.Code == VarianceCodes.BothConversionSite);
        Assert.Contains("could not be fully resolved", diagnostic.Message);
        Assert.True(preprocessor.Statistics.UnsafeUseSites >= 1);
    }

    [Fact]
    public void Mapped_readonly_to_mutable_reports_va012_with_member_names()
    {
        const string source =
            "using System.Collections.Generic; "
            + "class C { IReadOnlyList<string> Get() => null; }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        var diagnostic = Assert.Single(audit.Diagnostics, d => d.Code == VarianceCodes.MappedMutabilityExpansion);
        Assert.Contains("add", diagnostic.Message);
        Assert.Contains("java.util.List", diagnostic.Message);
    }

    [Fact]
    public void Source_defined_producer_interface_has_no_va012()
    {
        const string source =
            "interface IProducer<out T> { T Produce(); } "
            + "class C { IProducer<string> Get() => null; }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(0, CountCode(audit, VarianceCodes.MappedMutabilityExpansion));
    }

    [Fact]
    public void Mapped_mutability_check_can_be_disabled()
    {
        const string source =
            "using System.Collections.Generic; class C { IReadOnlyList<string> Get() => null; }";
        var compilation = Compile(source);
        var preprocessor = new VarianceAuditPreprocessor(new VarianceAuditOptions { CheckMappedMutability = false });
        preprocessor.BindCompilation(compilation);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(0, CountCode(audit, VarianceCodes.MappedMutabilityExpansion));
    }

    [Fact]
    public void Struct_constraint_reports_va020_and_default_dependency_va027()
    {
        const string source = "class C { void M<T>() where T : struct { T x = default(T); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var site = Assert.Single(analysis.ConstraintSites);
        Assert.Equal("struct", site.Kind);
        Assert.Equal("T", site.TypeParameterName);
        Assert.Equal(1, site.DependentDefaultSites);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.StructConstraint));
        Assert.Equal(1, CountCode(audit, VarianceCodes.DefaultOnValueTypeConstraint));
        Assert.Equal(1, preprocessor.Statistics.ConstraintsStruct);
    }

    [Fact]
    public void Default_on_unconstrained_type_parameter_reports_nothing()
    {
        const string source = "class C { void M<T>() { T x = default(T); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Empty(analysis.ConstraintSites);
        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(0, CountCode(audit, VarianceCodes.DefaultOnValueTypeConstraint));
    }

    [Fact]
    public void Unmanaged_constraint_reports_va021_only()
    {
        const string source = "class C { void M<T>() where T : unmanaged { } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var site = Assert.Single(analysis.ConstraintSites);
        Assert.Equal("unmanaged", site.Kind);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.UnmanagedConstraint));
        Assert.Equal(0, CountCode(audit, VarianceCodes.StructConstraint));
        Assert.Equal(1, preprocessor.Statistics.ConstraintsUnmanaged);
        Assert.Equal(0, preprocessor.Statistics.ConstraintsStruct);
    }

    [Fact]
    public void New_constraint_associates_activator_sites()
    {
        const string source =
            "class C { void M<T>() where T : new() { var t = System.Activator.CreateInstance<T>(); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var site = Assert.Single(analysis.ConstraintSites);
        Assert.Equal("new", site.Kind);
        Assert.Equal(1, site.ActivatorSites);
        Assert.Equal(1, site.DependentNewSites);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.NewConstraint));
        Assert.Equal(0, CountCode(audit, VarianceCodes.ResidualNewTSite));
    }

    [Fact]
    public void New_constraint_raw_form_registers_without_va023()
    {
        const string source = "class C { void M<T>() where T : new() { T t = new T(); } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var site = Assert.Single(analysis.ConstraintSites);
        Assert.Equal(1, site.DependentNewSites);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(0, CountCode(audit, VarianceCodes.ResidualNewTSite));
    }

    [Fact]
    public void Residual_new_with_initializer_reports_va023()
    {
        const string source =
            "interface IFoo { int X { get; set; } } "
            + "class C { void M<T>() where T : IFoo, new() { var t = new T { X = 1 }; } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        var site = Assert.Single(analysis.ConstraintSites);
        Assert.Equal("new", site.Kind);
        Assert.Equal(1, site.DependentNewSites);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.ResidualNewTSite));
    }

    [Fact]
    public void Notnull_constraint_reports_va024()
    {
        const string source = "class C { void M<T>() where T : notnull { } }";
        var (preprocessor, _, compilation) = Analyze(source);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.NotNullConstraint));
    }

    [Fact]
    public void Default_constraint_reports_va025()
    {
        const string source =
            "class B { public virtual void M<T>() where T : System.IComparable<T> { } } "
            + "class D : B { public override void M<T>() where T : default { } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Contains(analysis.ConstraintSites, site => site.Kind == "default");

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.DefaultConstraint));
    }

    [Fact]
    public void Class_constraint_reports_va026()
    {
        const string source = "class C { void M<T>() where T : class { } void N<T>() where T : class? { } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Equal(2, analysis.ConstraintSites.Count(site => site.Kind == "class"));

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(2, CountCode(audit, VarianceCodes.ClassConstraint));
    }

    [Fact]
    public void Upper_bound_constraint_produces_no_codes()
    {
        const string source = "class C { void M<T>() where T : System.IComparable<T> { } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Empty(analysis.ConstraintSites);
        var audit = AuditOf(preprocessor, compilation);
        Assert.Empty(audit.Diagnostics);
    }

    [Fact]
    public void Mixed_constraints_bucket_without_duplication()
    {
        // NOTE: `struct` cannot be combined with `new()` (CS0451), so value-type and
        // constructor buckets are exercised together with `class` here.
        const string source = "class C { void M<T>() where T : class, System.IComparable<T>, new() { } }";
        var (preprocessor, analysis, compilation) = Analyze(source);

        Assert.Equal(2, analysis.ConstraintSites.Count);
        Assert.Contains(analysis.ConstraintSites, site => site.Kind == "class");
        Assert.Contains(analysis.ConstraintSites, site => site.Kind == "new");
        Assert.Equal(0, preprocessor.Statistics.ConstraintsStruct);
        Assert.Equal(1, preprocessor.Statistics.ConstraintsNew);
        Assert.Equal(1, preprocessor.Statistics.ConstraintsClass);

        var audit = AuditOf(preprocessor, compilation);
        Assert.Equal(1, CountCode(audit, VarianceCodes.ClassConstraint));
        Assert.Equal(1, CountCode(audit, VarianceCodes.NewConstraint));
    }

    [Fact]
    public void Va070_report_written_is_info()
    {
        var compilation = Compile("interface IProducer<out T> { T Produce(); }");
        var preprocessor = new VarianceAuditPreprocessor();
        preprocessor.BindCompilation(compilation);
        preprocessor.Analyze();

        var diagnostic = preprocessor.BuildReportWrittenDiagnostic("out/variance-audit-report.json");
        Assert.Equal(VarianceSeverity.Info, diagnostic.Severity);
        Assert.Equal(VarianceCodes.ReportWritten, diagnostic.Code);
        Assert.Contains("variance-audit-report.json", diagnostic.Message);
    }

    [Fact]
    public void Report_json_carries_constraints_and_classification_byte_stable()
    {
        const string source =
            "using System.Collections.Generic; "
            + "interface IProducer<out T> { T Produce(); } "
            + "class C { void M<T>() where T : struct { } IReadOnlyList<string> Get() => null; }";
        var (preprocessor, _, _) = Analyze(source);

        var first = preprocessor.BuildReportJson();
        var second = preprocessor.BuildReportJson();
        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;

        var constraints = root.GetProperty("constraintSites").EnumerateArray().ToList();
        Assert.Single(constraints);
        Assert.Equal("struct", constraints[0].GetProperty("kind").GetString());

        var uses = root.GetProperty("useSites").EnumerateArray().ToList();
        Assert.All(uses, use =>
        {
            Assert.True(use.TryGetProperty("classification", out _));
            Assert.True(use.TryGetProperty("mappedJavaType", out _));
            Assert.True(use.TryGetProperty("mutatingMembers", out _));
        });
        var mappedUse = Assert.Single(uses, use => use.GetProperty("mappedJavaType").ValueKind == JsonValueKind.String);
        Assert.Equal("java.util.List", mappedUse.GetProperty("mappedJavaType").GetString());
        Assert.NotEmpty(mappedUse.GetProperty("mutatingMembers").EnumerateArray());

        var totals = root.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("constraintsStruct").GetInt32());
    }
}