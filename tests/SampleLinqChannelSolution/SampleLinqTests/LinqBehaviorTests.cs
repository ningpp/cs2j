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

    [Fact]
    public void Queryable_RecordingProvider_ReceivesQuotedQueryableCallSequence()
    {
        // Runtime half of the M3 gate: every operator must reach the custom
        // provider as a Queryable MethodCallExpression with the argument count
        // the BCL emits (lambda quoted as the 2nd argument).
        var shapes = QueryableScenarios.RecordedOperatorShapes();

        Assert.Equal(
            new[]
            {
                "Queryable.Where:2",
                "Queryable.Select:2",
                "Queryable.OrderBy:2",
                "Queryable.Take:2",
            },
            shapes);
    }

    [Fact]
    public void Queryable_MixedHostChain_PreservesClientSideSemantics()
    {
        var names = QueryableScenarios.MixedHostActiveNames(Things).ToArray();

        Assert.Equal(new[] { "alpha", "gamma" }, names);
    }

    [Fact]
    public void Queryable_TwoSourceQuerySyntax_JoinsById()
    {
        IQueryable<Thing> things = Things.AsQueryable();

        var names = QueryableScenarios
            .TwoSourceQuery(things, new[] { 1, 3 }.AsQueryable())
            .ToArray();

        Assert.Equal(new[] { "alpha", "gamma" }, names);
    }

    [Fact]
    public void Queryable_HandBuiltExpressionTree_PredicateCompilesAndRuns()
    {
        var isGamma = QueryableScenarios.BuildIdPredicate(3);

        Assert.False(isGamma(Things[0]));
        Assert.True(isGamma(Things[2]));
    }
}
