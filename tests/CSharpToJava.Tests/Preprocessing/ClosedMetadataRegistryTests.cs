using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.CLI.Preprocessing.Adapters;
using CSharpToJava.Core.ClosedMetadata;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;
using CSharpToJava.Core.StructValueSemantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>Registry contract for the closed-metadata (C30) M2' flip.</summary>
public sealed class ClosedMetadataRegistryTests
{
    private static PassChainConfiguration CreateConfig() => new()
    {
        MakerOptionsFactory = () => new ReadOnlyStructMakerOptions(),
        RecordReadOnlyOptionsFactory = () => new RecordReadOnlyOptions(),
        JavaKeywordOptionsFactory = () => new JavaKeywordOptions(),
        MemberDesugarOptionsFactory = () => new CSharpToJava.Core.MemberDesugar.MemberDesugarOptions(),
        StructValueSemanticsOptionsFactory = () => new StructValueSemanticsOptions(),
        EnumDeclarationOptionsFactory = () => new CSharpToJava.Core.EnumDeclaration.EnumDeclarationOptions(),
        LexicalNormalizeOptionsFactory = () => new CSharpToJava.Core.LexicalNormalize.LexicalNormalizeOptions(),
        EqualityConcatOptionsFactory = () => new CSharpToJava.Core.EqualityConcat.EqualityConcatOptions(),
        UnsafeFfmOptionsFactory = () => new CSharpToJava.Core.UnsafeFfm.UnsafeFfmOptions(),
        ExpressionDesugarOptionsFactory = () => new CSharpToJava.Core.ExpressionDesugar.ExpressionDesugarOptions(),
        VarianceAuditOptionsFactory = () => new CSharpToJava.Core.VarianceAudit.VarianceAuditOptions(),
    };

    [Fact]
    public void ClosedMetadata_Is_Implemented_DefaultOn_WithC30CatalogItems()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.ClosedMetadata);
        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal(PassIds.ClosedMetadata, descriptor.Id);
        Assert.Equal("Closed-type metadata registration", descriptor.DisplayName);
        Assert.Equal(".cs2j-closed-metadata-src", descriptor.IntermediateDirectoryName);
        Assert.Equal(PassStage.Abi, descriptor.Stage);
        Assert.True(descriptor.DefaultEnabled);
        Assert.Equal(new[] { PassIds.GenericDeepen }, descriptor.RunAfterIds);
        Assert.Equal(PassSupportLibrary.None, descriptor.SupportLibrary);
        Assert.True(descriptor.NeedsSemanticModel);
        Assert.True(descriptor.VerifyBuildByDefault);
        Assert.True(descriptor.VerifyTestsByDefault);
        Assert.Equal("C30", descriptor.CatalogItems);
    }

    [Fact]
    public void ClosedMetadata_IsNotAStub_AndInTheDefaultPlan()
    {
        var registry = PassCatalog.BuildRegistry();

        Assert.DoesNotContain(registry.Ordered, pass =>
            pass.Id == PassIds.ClosedMetadata && pass.Status == PassStatus.Stub);

        var plan = PassPlanResolver.Resolve(registry);
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Enabled, pass => pass.Id == PassIds.ClosedMetadata);
    }

    [Fact]
    public void ClosedMetadata_WhenExplicitlyEnabled_JoinsThePlanAfterGenericDeepen()
    {
        // --enable-pass closed-metadata over the default chain: valid plan, no
        // CS2J9001 (M2' flip retired the stub rejection), locked slot after C02.
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            enablePassIds: new[] { PassIds.ClosedMetadata });

        Assert.True(plan.IsValid);
        var ids = plan.Enabled.Select(p => p.Id).ToList();
        var cm = ids.IndexOf(PassIds.ClosedMetadata);
        Assert.True(cm > 0, "closed-metadata must be planned when explicitly enabled");
        Assert.Equal(PassIds.GenericDeepen, ids[cm - 1]);
    }

    [Fact]
    public void ClosedMetadata_IsolatedEnable_RunsAloneInItsSlot()
    {
        var registry = PassCatalog.BuildRegistry();
        var allOff = PassChainFactory.BuildAllDisabledToggles(registry)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var plan = PassPlanResolver.Resolve(
            registry,
            allOff,
            enablePassIds: new[] { PassIds.ClosedMetadata });

        Assert.True(plan.IsValid);
        Assert.Equal(
            new[] { PassIds.ClosedMetadata },
            plan.Enabled.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Factory_CreatesAdapter_ForClosedMetadata()
    {
        var adapter = Assert.IsType<ClosedMetadataPassAdapter>(
            PassChainFactory.CreateAdapter(PassIds.ClosedMetadata, CreateConfig()));
        Assert.Equal(PassIds.ClosedMetadata, adapter.Descriptor.Id);
        Assert.True(adapter.VerifyBuild);
        Assert.True(adapter.VerifyTests);
        Assert.True(adapter.DeleteBuildOutputsAfterRestore);
    }

    [Fact]
    public async Task CompilationReady_WithoutGenericDeepen_Reports_CS2J1205_Error()
    {
        // Fail closed: no plan information at all counts as "generic-deepen absent".
        foreach (var enabledIds in new IReadOnlySet<string>?[]
                 {
                     new HashSet<string>(StringComparer.Ordinal) { PassIds.ClosedMetadata },
                     null,
                 })
        {
            var adapter = new ClosedMetadataPassAdapter(
                new ClosedMetadataOptions { EnabledPassIds = enabledIds },
                verbose: false,
                filterUnsupportedProjects: true,
                compatNamespacePrefixes: null,
                verifyBuild: true,
                verifyTests: true);

            await adapter.CompilationReadyAsync(CSharpCompilation.Create("cs2j-closed-metadata-test"));

            var diagnostic = Assert.Single(adapter.Diagnostics);
            Assert.Equal(ClosedMetadataCodes.GenericDeepenRequired, diagnostic.Code);
            Assert.Equal(ClosedMetadataSeverity.Error, diagnostic.Severity);
            Assert.True(adapter.HasErrors);
        }
    }

    [Fact]
    public async Task CompilationReady_WithGenericDeepen_Analyzes_WithoutDiagnostics()
    {
        var adapter = new ClosedMetadataPassAdapter(
            new ClosedMetadataOptions
            {
                EnabledPassIds = new HashSet<string>(StringComparer.Ordinal)
                {
                    PassIds.GenericDeepen,
                    PassIds.ClosedMetadata,
                },
            },
            verbose: false,
            filterUnsupportedProjects: true,
            compatNamespacePrefixes: null,
            verifyBuild: true,
            verifyTests: true);

        await adapter.CompilationReadyAsync(CSharpCompilation.Create("cs2j-closed-metadata-test"));

        Assert.Empty(adapter.Diagnostics);
        Assert.False(adapter.HasErrors);

        // M2' skeleton contract: empty plan, zero rewrites (null = clean byte
        // copy), no generated files, zero residual findings, zero statistics.
        Assert.Empty(adapter.Diagnostics);
        Assert.Equal(0, adapter.Statistics.ClosedTypesEnumerated);
        Assert.Equal(0, adapter.Statistics.Registered);
        Assert.Equal(0, adapter.Statistics.Deduplicated);
        Assert.Equal(0, adapter.Statistics.RegisterFilesGenerated);
        Assert.Equal(0, adapter.Statistics.ConsumerSitesLinked);
        Assert.Equal(0, adapter.Statistics.DynamicSitesSkipped);
        Assert.StartsWith("closed-metadata: 0 registered", adapter.BuildChainSummaryLine());

        var rewrite = await adapter.RewriteFileAsync(new PassFileContext
        {
            SourceCode = "class C { }",
            SourceFilePath = "C.cs",
            RelativePath = "C.cs",
            DestinationFilePath = "C.cs",
            SemanticModel = null,
        });
        Assert.Null(rewrite);
    }
}
