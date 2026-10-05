using SampleLinqLib;
using Xunit;

namespace SampleLinqTests;

public sealed class LinqBehaviorTests
{
    private static readonly Thing[] Things =
    [
        new(1, "alpha", true),
        new(2, "beta", false),
        new(3, "gamma", true),
    ];

    [Fact]
    public void Enumerable_ActiveNames_ReturnsSortedActiveNames()
    {
        var names = LinqTools.ActiveNames(Things).ToArray();

        Assert.Equal(new[] { "alpha", "gamma" }, names);
    }

    [Fact]
    public void Enumerable_ActiveCount_OnlyCountsActive()
        => Assert.Equal(2, LinqTools.ActiveCount(Things));

    [Fact]
    public void Enumerable_DoubledEvens_AppliesFilterThenProjection()
        => Assert.Equal(new[] { 4, 8 }, LinqTools.DoubledEvens(new[] { 1, 2, 3, 4 }));

    [Fact]
    public void Queryable_Repository_QueryActive_PreservesQueryableSemantics()
    {
        IThingRepository repository = new InMemoryThingRepository(Things);

        var active = repository.QueryActive().Select(t => t.Name).ToArray();

        Assert.Equal(new[] { "alpha", "gamma" }, active);
    }
}
