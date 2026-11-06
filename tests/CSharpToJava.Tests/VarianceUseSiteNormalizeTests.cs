using System.Text.Json;
using CSharpToJava.CLI;
using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.LinqChannel;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.VarianceUseSite;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace CSharpToJava.Tests;

/// <summary>
/// C33 R4 <c>variance-use-site-normalize</c> (VR) pass: VR-01/02 rewrites,
/// VR-04 array preservation, VR-06/08 DeclaredUnsupported, pass-level
/// idempotency/byte preservation and the registry/plan slot.
/// </summary>
public sealed class VarianceUseSiteNormalizeTests
{
    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trusted is null)
        {
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        }

        return trusted.Split(Path.PathSeparator)
            .Where(File.Exists)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static CSharpCompilation Compile(string source, string fileName = "Test.cs")
    {
        var tree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview),
            path: fileName);
        return CSharpCompilation.Create(
            "variance-use-site-normalize-test-" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static (VarianceUseSiteNormalizeResult Result, VarianceUseSiteNormalizePreprocessor Pass)
        Run(string source)
    {
        var compilation = Compile(source);
        var tree = compilation.SyntaxTrees.Single();
        var pass = new VarianceUseSiteNormalizePreprocessor();
        pass.BindCompilation(compilation);
        var result = pass.RewriteFile(source, compilation.GetSemanticModel(tree), "Test.cs");
        return (result, pass);
    }

    private static int ErrorCount(string source)
    {
        var compilation = Compile(source);
        return compilation.GetDiagnostics()
            .Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    // ---- VR-01 / VR-02 positive rewrites ----

    [Fact]
    public void Producer_covariant_argument_is_rewritten_to_an_explicit_object_cast()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static void M(IEnumerable<object> x) { }
                static void N(IEnumerable<string> s) { M(s); }
            }
            """;

        var (result, pass) = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("(object)s", result.OutputCode);
        Assert.Equal(0, ErrorCount(result.OutputCode));

        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.ProducerConversion, site.Rule);
        Assert.Equal(VarianceUseSiteActions.Rewritten, site.Action);
        Assert.True(site.Rewritten);
        Assert.Contains("IEnumerable", site.SourceType);
        Assert.Contains("IEnumerable", site.TargetType);

        Assert.Equal(1, pass.Statistics.SitesRewritten);
        Assert.Equal(1, pass.Statistics.RewrittenByRule[VarianceUseSiteRules.ProducerConversion]);
    }

    [Fact]
    public void Consumer_contravariant_argument_is_rewritten_to_an_explicit_object_cast()
    {
        const string source = """
            using System;
            class C
            {
                static void M(Action<string> a) { }
                static void N(Action<object> ao) { M(ao); }
            }
            """;

        var (result, _) = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("(object)ao", result.OutputCode);
        Assert.Equal(0, ErrorCount(result.OutputCode));

        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.ConsumerConversion, site.Rule);
        Assert.Equal(VarianceUseSiteActions.Rewritten, site.Action);
    }

    [Fact]
    public void Producer_return_site_is_rewritten()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static IEnumerable<object> N(IEnumerable<string> s) { return s; }
            }
            """;

        var (result, _) = Run(source);

        Assert.True(result.Changed);
        Assert.Contains("(object)s", result.OutputCode);
        Assert.Equal(0, ErrorCount(result.OutputCode));

        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.ProducerConversion, site.Rule);
        Assert.True(site.Rewritten);
    }

    // ---- negative sites: write-back/Both flow ----

    [Fact]
    public void Both_flow_local_initializer_is_declared_unsupported_without_rewrite()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static void M(IEnumerable<object> x) { }
                static void Use(IEnumerable<object> x) { }
                static void N(IEnumerable<string> s)
                {
                    IEnumerable<object> o = s;
                    Use(o);
                }
            }
            """;

        var (result, pass) = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.BothFlow, site.Rule);
        Assert.Equal(VarianceUseSiteActions.DeclaredUnsupported, site.Action);
        Assert.Equal(nameof(VarianceUseSiteDiagnosticCode.CS2J1404), site.Code);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == VarianceUseSiteDiagnosticCode.CS2J1404
                 && d.Severity == VarianceUseSiteSeverity.Warning);
        Assert.Equal(1, pass.Statistics.SitesDeclaredUnsupported);
    }

    // ---- VR-04 array covariance is preserved ----

    [Fact]
    public void Reference_array_covariance_is_preserved_and_verified()
    {
        const string source = """
            class C
            {
                static void M(object[] x) { }
                static void N(string[] s) { M(s); }
            }
            """;

        var (result, pass) = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);

        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.ArrayCovariance, site.Rule);
        Assert.Equal(VarianceUseSiteActions.Preserved, site.Action);
        Assert.False(site.Rewritten);
        Assert.Equal("Array", site.Direction);
        Assert.Equal(1, pass.Statistics.SitesPreserved);
        Assert.Equal(1, pass.Statistics.PreservedByRule[VarianceUseSiteRules.ArrayCovariance]);
    }

    [Fact]
    public void Array_identity_edge_without_a_bound_candidate_is_preserved_as_VR04()
    {
        // The shared audit reports a reference-array edge even when source and
        // target are the same array type (object[] -> object[]); the VR pass must
        // not turn that identity edge into an unsupported rewrite.
        const string source = """
            using System;
            class C
            {
                static object[] N(Type t) { object[] a = t.GetCustomAttributes(false); return a; }
            }
            """;

        var (result, _) = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(2, result.Sites.Count);
        Assert.All(result.Sites, site =>
        {
            Assert.Equal(VarianceUseSiteRules.ArrayCovariance, site.Rule);
            Assert.Equal(VarianceUseSiteActions.Preserved, site.Action);
            Assert.Null(site.NormCode);
        });
    }

    // ---- VR-06 / VR-03 pair classification ----

    [Fact]
    public void Value_type_type_argument_is_classified_as_VR06()
    {
        var compilation = Compile("using System.Collections.Generic; class C { }");
        var enumerable = compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IEnumerable`1")!;
        var source = enumerable.Construct(
            compilation.GetSpecialType(SpecialType.System_Int32));
        var target = enumerable.Construct(
            compilation.GetSpecialType(SpecialType.System_Object));

        Assert.Equal(
            VarianceUseSiteNormalizePreprocessor.VariancePairKind.ValueType,
            VarianceUseSiteNormalizePreprocessor.ClassifyPair(source, target));
    }

    [Fact]
    public void Nested_invariant_type_argument_is_classified_as_VR03()
    {
        var compilation = Compile("using System.Collections.Generic; class C { }");
        var enumerable = compilation.GetTypeByMetadataName(
            "System.Collections.Generic.IEnumerable`1")!;
        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;
        var obj = compilation.GetSpecialType(SpecialType.System_Object);
        var str = compilation.GetSpecialType(SpecialType.System_String);
        var source = enumerable.Construct(list.Construct(str));
        var target = enumerable.Construct(list.Construct(obj));

        Assert.Equal(
            VarianceUseSiteNormalizePreprocessor.VariancePairKind.NestedInvariant,
            VarianceUseSiteNormalizePreprocessor.ClassifyPair(source, target));
    }

    // ---- VR-08 dynamic/unbound ----

    [Fact]
    public void Dynamic_argument_is_declared_unsupported_as_VR08()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static void M(IEnumerable<object> x) { }
                static void N(dynamic d) { M(d); }
            }
            """;

        var (result, _) = Run(source);

        Assert.False(result.Changed);
        var site = Assert.Single(result.Sites);
        Assert.Equal(VarianceUseSiteRules.DynamicOrUnbound, site.Rule);
        Assert.Equal(VarianceUseSiteActions.DeclaredUnsupported, site.Action);
        Assert.Equal(nameof(VarianceUseSiteDiagnosticCode.CS2J1404), site.Code);
        Assert.Equal(PassConstructIds.GenericVarianceUse, site.NormCode);
        Assert.Equal("Unresolved", site.Direction);
    }

    // ---- idempotency / clean-file byte preservation ----

    [Fact]
    public void Second_run_over_rewritten_output_is_a_fixed_point()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static void M(IEnumerable<object> x) { }
                static void N(IEnumerable<string> s) { M(s); }
            }
            """;

        var (first, _) = Run(source);
        Assert.True(first.Changed);

        var compilation = Compile(first.OutputCode);
        var pass = new VarianceUseSiteNormalizePreprocessor();
        pass.BindCompilation(compilation);
        var second = pass.RewriteFile(
            first.OutputCode,
            compilation.GetSemanticModel(compilation.SyntaxTrees.Single()),
            "Test.cs");

        Assert.False(second.Changed);
        Assert.Equal(first.OutputCode, second.OutputCode);
        Assert.Empty(second.Sites);
    }

    [Fact]
    public void Clean_file_is_returned_byte_for_byte()
    {
        const string source = "class C { int M(int x) => x + 1; }";

        var (result, pass) = Run(source);

        Assert.False(result.Changed);
        Assert.Equal(source, result.OutputCode);
        Assert.Empty(result.Sites);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, pass.Statistics.FilesSkippedClean);
    }

    // ---- report ----

    [Fact]
    public void Report_json_is_parseable_stable_and_lists_rule_reason_and_action()
    {
        const string source = """
            using System.Collections.Generic;
            class C
            {
                static void M(IEnumerable<object> x) { }
                static void N(IEnumerable<string> s) { M(s); }
            }
            """;

        var (_, pass) = Run(source);

        var first = pass.BuildReportJson();
        var second = pass.BuildReportJson();
        Assert.Equal(first, second);

        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());

        var sites = root.GetProperty("sites").EnumerateArray().ToList();
        var site = Assert.Single(sites);
        Assert.Equal(VarianceUseSiteRules.ProducerConversion, site.GetProperty("rule").GetString());
        Assert.Equal("Rewritten", site.GetProperty("action").GetString());
        Assert.True(site.GetProperty("rewritten").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(site.GetProperty("safety").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(site.GetProperty("sourceType").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(site.GetProperty("targetType").GetString()));

        var totals = root.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("sitesTotal").GetInt32());
        Assert.Equal(1, totals.GetProperty("sitesRewritten").GetInt32());
        Assert.Equal(
            1,
            totals.GetProperty("rewrittenByRule")
                .GetProperty(VarianceUseSiteRules.ProducerConversion)
                .GetInt32());
    }

    // ---- registry / plan / list-passes ----

    [Fact]
    public void Descriptor_is_implemented_default_on_and_slots_after_equality_concat()
    {
        var registry = PassCatalog.BuildRegistry();

        var descriptor = PassCatalog.ImplementedById(PassIds.VarianceUseSiteNormalize);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(".cs2j-variance-use-site-normalize-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Expression, descriptor.Stage);
        Assert.Equal(
            new[] { PassIds.MemberDesugar, PassIds.LinqChannel, PassIds.EqualityConcat },
            descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C33", descriptor.CatalogItems);
        Assert.NotNull(descriptor.Contract);
        Assert.Contains(
            PassConstructIds.GenericVarianceUse, descriptor.Contract!.Consumes);
        Assert.Contains(
            PassConstructIds.GenericVarianceUse, descriptor.Contract.MayLeave);

        var ordered = registry.Ordered
            .Where(pass => pass.Status == PassStatus.Implemented)
            .Select(pass => pass.Id)
            .ToList();
        var equality = ordered.IndexOf(PassIds.EqualityConcat);
        var variance = ordered.IndexOf(PassIds.VarianceUseSiteNormalize);
        var platform = ordered.IndexOf(PassIds.PlatformIsolation);
        var javaKeywords = ordered.IndexOf(PassIds.JavaKeywords);
        Assert.True(equality >= 0 && variance > equality, "expected equality-concat < variance-use-site-normalize");
        Assert.True(variance < platform, "expected variance-use-site-normalize < platform-isolation");
        Assert.True(platform < javaKeywords, "expected variance-use-site-normalize < java-keywords");

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Enabled, pass => pass.Id == PassIds.VarianceUseSiteNormalize);

        var disabled = PassPlanResolver.Resolve(
            registry, disablePassIds: new[] { PassIds.VarianceUseSiteNormalize });
        Assert.True(disabled.IsValid);
        Assert.DoesNotContain(disabled.Enabled, pass => pass.Id == PassIds.VarianceUseSiteNormalize);

        var line = Assert.Single(
            PassListFormatter.BuildLines(registry),
            row => row.TrimStart().StartsWith(
                PassIds.VarianceUseSiteNormalize, StringComparison.Ordinal));
        Assert.Contains("Implemented", line);
        Assert.Contains(" on ", line);
        Assert.Contains("rules=C33", line);
        Assert.Contains(PassIds.EqualityConcat, line);
    }

    [Fact]
    public void ChainFactory_creates_the_adapter_with_verification_defaults()
    {
        var config = new PassChainConfiguration
        {
            MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
            RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
            JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
            MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
            StructValueSemanticsOptionsFactory = () => new CSharpToJava.Core.StructValueSemantics.StructValueSemanticsOptions(),
            VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
            EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
            LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
            EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
            UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
            ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
            LinqChannelOptionsFactory = () => new LinqChannelOptions(),
        };

        var adapter = PassChainFactory.CreateAdapter(
            PassIds.VarianceUseSiteNormalize, config);

        Assert.Equal(PassIds.VarianceUseSiteNormalize, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
    }
}
