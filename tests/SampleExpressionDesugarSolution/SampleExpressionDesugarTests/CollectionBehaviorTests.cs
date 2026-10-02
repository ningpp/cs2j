using System.Collections.Immutable;
using SampleExpressionDesugarLib;
using Xunit;

namespace SampleExpressionDesugarTests;

/// <summary>
/// Runtime equivalence gate: these assertions must pass both BEFORE the
/// expression-desugar pass (native C# collection-expression semantics) and
/// AFTER it (the chain runs dotnet test on the intermediate tree).
/// </summary>
public static class CollectionBehaviorTests
{
    [Fact]
    public static void MutableConcreteTargets()
    {
        Assert.Equal(new[] { 1, 2, 9 }, MutableTargets.ListLiteral(9));

        Assert.Equal(
            new[] { 7, 8, 9, 10 },
            MutableTargets.ListWithSpread(new[] { 7, 8 }, 9));

        var bag = MutableTargets.CustomAddBag();
        Assert.Equal(3, bag.Count);
        Assert.Equal(new[] { "a", "b", "c" }, bag);

        var nested = MutableTargets.NestedLists(new[] { 8, 9 });
        Assert.Equal(3, nested.Count);
        Assert.Equal(new[] { 1 }, nested[0]);
        Assert.Equal(new[] { 8, 9, 2 }, nested[1]);
        Assert.Equal(new[] { 3, 4 }, nested[2]);

        var matrix = MutableTargets.Matrix();
        Assert.Equal(2, matrix.Count);
        Assert.Equal(new[] { 1, 2 }, matrix[0]);
        Assert.Equal(new[] { 3, 4 }, matrix[1]);
    }

    [Fact]
    public static void InterfaceTargetsAllFive()
    {
        Assert.Equal(new[] { 1, 2, 3 }, InterfaceTargets.AsEnumerable());
        Assert.Equal(new[] { 4, 5 }, InterfaceTargets.AsCollection());
        Assert.Equal(new[] { 6 }, InterfaceTargets.AsList());
        Assert.Equal(new[] { 7, 8 }, InterfaceTargets.AsReadOnlyCollection());
        Assert.Equal(new[] { 9, 10, 11 }, InterfaceTargets.AsReadOnlyList());

        Assert.Equal(
            13,
            InterfaceTargets.SpreadToEnumerable(new List<int> { 1, 9 }, 3).Sum());

        var holder = new InterfaceHolder();
        Assert.Equal(new[] { 10, 20 }, holder.EnumerableField);
        Assert.Equal(new[] { "x", "y" }, holder.ReadOnlyCollectionProperty);
    }

    [Fact]
    public static void SpanTargetsBecomeHeapArrays()
    {
        Assert.Equal(16, SpanTargets.MutateAndSum());
        Assert.Equal(0, SpanTargets.EmptyBytesLength());
        Assert.Equal(6, SpanTargets.LiteralArgument());
        Assert.Equal(15, SpanTargets.WithSpreadSum(new[] { 4, 5 }, 6));
    }

    [Fact]
    public static void CollectionBuilderFactories()
    {
        Assert.Equal(3, BuilderSites.NoSpread().Count);
        Assert.Equal(new[] { 1, 2, 3 }, BuilderSites.NoSpread());
        Assert.Equal(0, BuilderSites.Empty().Count);

        var spread = BuilderSites.WithSpread(new[] { 2, 4 }, 6);
        Assert.Equal(3, spread.Count);
        Assert.Equal(new[] { 2, 4, 6 }, spread);

        var map = BuilderSites.MapLiteral();
        Assert.Equal(2, map.Count);

        Assert.Equal(
            new[] { 1, 2, 3 },
            BuilderSites.Immutable().ToArray());
        Assert.Equal(0, BuilderSites.ImmutableEmpty().Length);
    }

    [Fact]
    public static void NestedDto()
    {
        var order = NestedBuild.Build();
        Assert.Equal("o", order.Name);
        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(1, order.Lines[0].ProductId);
        Assert.Equal(2, order.Lines[0].Quantity);
        Assert.Equal(3, order.Lines[1].ProductId);
        Assert.Equal(new[] { "a", "b", "c", "d" }, order.Tags);
    }

    [Fact]
    public static void EmptyMatrix()
    {
        Assert.Empty(EmptyForms.EmptyArray());
        Assert.Empty(EmptyForms.EmptyList());
        Assert.Empty(EmptyForms.EmptyInterface());
        Assert.Equal(0, EmptyForms.EmptySpanLength());

        var emptyArray = Array.Empty<int>();
        var emptyList = new List<int>();
        IEnumerable<int> emptyEnumerable = Array.Empty<int>();

        Assert.Empty(EmptyForms.SpreadEmptyArray(emptyArray));
        Assert.Empty(EmptyForms.SpreadEmptyList(emptyList));
        Assert.Empty(EmptyForms.SpreadEmptyInterface(emptyEnumerable));
        Assert.Equal(0, EmptyForms.SpreadEmptySpanLength(emptyArray));
    }
}
