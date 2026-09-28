using CSharpToJava.Core.Preprocessing;

namespace CSharpToJava.Tests.Preprocessing;

public sealed class PassRegistryTests
{
    private static PassDescriptor Desc(
        string id,
        bool enabled = false,
        PassStatus status = PassStatus.Implemented,
        IReadOnlyList<string>? runAfter = null,
        string? dir = null)
        => new(
            id,
            id + " display",
            dir ?? (".cs2j-" + id + "-src"),
            PassStage.Expression,
            status,
            enabled,
            runAfter);

    [Fact]
    public void Orders_by_RunAfter_and_keeps_declaration_order_for_ties()
    {
        var registry = PassRegistry.Create(new[]
        {
            Desc("d", runAfter: new[] { "c" }),
            Desc("a"),
            Desc("b", runAfter: new[] { "a" }),
            Desc("c", runAfter: new[] { "b" }),
        });

        Assert.Equal(
            new[] { "a", "b", "c", "d" },
            registry.Ordered.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Rejects_duplicate_ids()
    {
        var ex = Assert.Throws<PassRegistryException>(() => PassRegistry.Create(new[]
        {
            Desc("dup", dir: ".cs2j-a-src"),
            Desc("dup", dir: ".cs2j-b-src"),
        }));
        Assert.Contains("Duplicate pass id", ex.Message);
    }

    [Fact]
    public void Rejects_duplicate_intermediate_directories()
    {
        var ex = Assert.Throws<PassRegistryException>(() => PassRegistry.Create(new[]
        {
            Desc("a", dir: ".cs2j-shared-src"),
            Desc("b", dir: ".cs2j-shared-src"),
        }));
        Assert.Contains("Duplicate intermediate directory", ex.Message);
    }

    [Fact]
    public void Rejects_unknown_RunAfter_reference()
    {
        var ex = Assert.Throws<PassRegistryException>(() => PassRegistry.Create(new[]
        {
            Desc("a", runAfter: new[] { "ghost" }),
        }));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Rejects_cycles()
    {
        var ex = Assert.Throws<PassRegistryException>(() => PassRegistry.Create(new[]
        {
            Desc("a", runAfter: new[] { "b" }),
            Desc("b", runAfter: new[] { "a" }),
        }));
        Assert.Contains("Cycle detected", ex.Message);
    }

    [Fact]
    public void Lookups_and_status_filters_work()
    {
        var registry = PassRegistry.Create(new[]
        {
            Desc("impl", status: PassStatus.Implemented),
            Desc("todo", status: PassStatus.Stub),
        });

        Assert.True(registry.TryGet("impl", out var descriptor));
        Assert.Equal("impl", descriptor.Id);
        Assert.Throws<KeyNotFoundException>(() => registry.Require("nope"));
        Assert.NotNull(registry.Require("impl"));
        Assert.Equal(new[] { "todo" }, registry.WhereStatus(PassStatus.Stub).Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "impl" }, registry.WhereStatus(PassStatus.Implemented).Select(p => p.Id).ToArray());
    }
}
