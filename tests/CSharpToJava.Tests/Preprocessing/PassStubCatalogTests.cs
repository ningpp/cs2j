using CSharpToJava.CLI.Preprocessing;
using CSharpToJava.Core.JavaKeywords;
using CSharpToJava.Core.Preprocessing;
using CSharpToJava.Core.ReadOnlyStructMaker;
using CSharpToJava.Core.RecordReadOnly;

namespace CSharpToJava.Tests.Preprocessing;

/// <summary>
/// Step 8: the merged-family stubs must be visible and disabled by default,
/// must never join the default plan, and must fail loudly (CS2J9001) the
/// moment they are enabled — without touching the filesystem.
/// </summary>
public sealed class PassStubCatalogTests
{
    private static PassDescriptor[] Stubs =>
        PassCatalog.BuildRegistry().WhereStatus(PassStatus.Stub).ToArray();

    [Fact]
    public void All_merged_families_are_registered_as_stubs()
    {
        var ids = Stubs.Select(p => p.Id).ToArray();

        Assert.Equal(22, ids.Length);
        Assert.DoesNotContain(PassIds.PatternDesugar, ids);
        Assert.Contains(PassStubCatalog.Ids.ClosureLift, ids);
        Assert.Contains(PassStubCatalog.Ids.LinqChannel, ids);
        Assert.Contains(PassStubCatalog.Ids.DynamicDesugar, ids);
        Assert.Contains(PassStubCatalog.Ids.ClosedMetadata, ids);
        Assert.Contains(PassStubCatalog.Ids.VarianceAudit, ids);
    }

    [Fact]
    public void PatternDesugar_IsImplemented_AndNotAStub()
    {
        var descriptor = PassCatalog.ImplementedById(PassIds.PatternDesugar);

        Assert.Equal(PassStatus.Implemented, descriptor.Status);
        Assert.Equal("C03, C37", descriptor.CatalogItems);
        Assert.Equal(".cs2j-pattern-desugar-src", descriptor.IntermediateDirectoryName);
        Assert.DoesNotContain(Stubs, stub => stub.Id == PassIds.PatternDesugar);
    }

    [Fact]
    public void Every_stub_is_disabled_by_default()
    {
        Assert.All(Stubs, stub => Assert.False(
            stub.DefaultEnabled,
            $"stub '{stub.Id}' must stay opt-in until implemented"));
    }

    [Fact]
    public void Every_stub_has_a_unique_intermediate_directory()
    {
        var directories = Stubs.Select(p => p.IntermediateDirectoryName).ToArray();

        Assert.Equal(directories.Length, directories.Distinct(StringComparer.Ordinal).Count());
        Assert.All(directories, directory => Assert.StartsWith(".cs2j-", directory));
        Assert.All(directories, directory => Assert.EndsWith("-src", directory));
    }

    [Fact]
    public void Default_plan_contains_no_stubs()
    {
        var plan = PassPlanResolver.Resolve(PassCatalog.BuildRegistry());

        Assert.True(plan.IsValid);
        Assert.DoesNotContain(plan.Enabled, pass => pass.Status == PassStatus.Stub);
    }

    [Fact]
    public void Enabling_a_stub_resolves_to_an_enabled_plan_entry()
    {
        var plan = PassPlanResolver.Resolve(
            PassCatalog.BuildRegistry(),
            legacyToggles: PassChainFactory.BuildAllDisabledToggles(PassCatalog.BuildRegistry()),
            enablePassIds: new[] { PassStubCatalog.Ids.ClosureLift });

        Assert.True(plan.IsValid);
        var enabled = Assert.Single(plan.Enabled);
        Assert.Equal(PassStubCatalog.Ids.ClosureLift, enabled.Id);
        Assert.Equal(PassStatus.Stub, enabled.Status);
    }

    [Fact]
    public async Task Running_an_enabled_stub_fails_with_CS2J9001_and_creates_no_directories()
    {
        var destinationRoot = Path.Combine(Path.GetTempPath(), "cs2j-stub-test-" + Guid.NewGuid().ToString("N"));
        var originalError = Console.Error;
        string captured;
        try
        {
            using var errorWriter = new StringWriter();
            Console.SetError(errorWriter);

            var result = await CliPreprocessChain.RunAsync(new CliChainRequest
            {
                SourcePath = Path.Combine(Path.GetTempPath(), "cs2j-stub-test-source-does-not-exist"),
                DestinationRoot = destinationRoot,
                MakerOptionsFactory = () => throw new InvalidOperationException("stub must run first"),
                RecordReadOnlyOptionsFactory = () => throw new InvalidOperationException("stub must run first"),
                JavaKeywordOptionsFactory = () => throw new InvalidOperationException("stub must run first"),
                MemberDesugarOptionsFactory = () => throw new InvalidOperationException("stub must run first"),
                LegacyToggles = PassChainFactory.BuildAllDisabledToggles(PassCatalog.BuildRegistry()),
                EnablePassIds = new[] { PassStubCatalog.Ids.ClosureLift },
            });

            captured = errorWriter.ToString();
            Assert.False(result.Success);
        }
        finally
        {
            Console.SetError(originalError);
        }

        Assert.Contains(PreprocessDiagnosticCodes.StubNotImplemented, captured);
        Assert.Contains(PassStubCatalog.Ids.ClosureLift, captured);
        Assert.False(Directory.Exists(destinationRoot), "an enabled stub must not create any output directory");
    }
}
