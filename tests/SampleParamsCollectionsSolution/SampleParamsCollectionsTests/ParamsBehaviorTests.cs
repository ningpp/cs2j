using System.Collections.Generic;
using SampleParamsCollectionsLib;
using Xunit;

namespace SampleParamsCollectionsTests;

/// <summary>
/// .NET-side behavior contract for the params-collections fixture APIs. These
/// tests run twice: once on the original source (baseline) and once on the
/// strip-default intermediate tree with the C48 rule enabled — the assertions
/// must hold in both worlds (design §7.2 half-region equivalence): content /
/// order / Count, empty-state identity, form-B reference identity, writable
/// spans, side-effect ordering.
/// </summary>
public class ParamsBehaviorTests
{
    // ------------------------------------------------------------------
    // Content / order / Count
    // ------------------------------------------------------------------

    [Fact]
    public void CommaExpansion_ContentAndOrder()
    {
        Assert.Equal("1|2|3", ParamsLib.FormatInts(1, 2, 3));
        Assert.Equal(10, ParamsLib.SumAll(1, 2, 3, 4));
        Assert.Equal("1,2,3", string.Join(",", ParamsLib.Batches(1, 2, 3)));
        Assert.Equal(0, ParamsLib.Sum(7, 8, 9) - 24);
    }

    [Fact]
    public void EmptyState_YieldsEmptyCollections()
    {
        Assert.Equal(string.Empty, ParamsLib.FormatInts());
        Assert.Equal(0, ParamsLib.SumAll());
        Assert.Equal(0, ParamsLib.Sum());
        Assert.Empty(ParamsLib.Batches());
    }

    [Fact]
    public void EmptyState_ArrayEmptySingleton_SameInstanceAcrossCalls()
    {
        // K1 (D8 off): the compiler lowering is unchanged — Array.Empty singleton.
        ParamsLib.Sum();
        var firstArray = ParamsLib.LastArray;
        ParamsLib.Sum();
        Assert.NotNull(firstArray);
        Assert.True(ReferenceEquals(firstArray, ParamsLib.LastArray),
            "empty params int[] must reuse the Array.Empty<int> singleton");
        Assert.True(ReferenceEquals(firstArray, System.Array.Empty<int>()));

        // K5: the preprocessed tree fills the empty slot with
        // System.Array.Empty<int>() explicitly; the compiler does the same on
        // the original source — the received instance matches in both worlds.
        ParamsLib.SumAll();
        var firstEnumerable = ParamsLib.LastEnumerable;
        ParamsLib.SumAll();
        Assert.True(ReferenceEquals(firstEnumerable, ParamsLib.LastEnumerable));
        Assert.True(ReferenceEquals(firstEnumerable, System.Array.Empty<int>()));
    }

    [Fact]
    public void EmptyState_ListIsAFreshInstancePerCall()
    {
        // K4: both the compiler lowering and the C48 emission allocate a new
        // List<int> per empty call — never a shared singleton.
        ParamsLib.Batches();
        var firstList = ParamsLib.LastList;
        ParamsLib.Batches();
        Assert.NotNull(firstList);
        Assert.False(ReferenceEquals(firstList, ParamsLib.LastList),
            "empty params-collection List<int> must allocate a fresh list per call");
    }

    // ------------------------------------------------------------------
    // Form B identity: the callee mutates the very object the caller passed.
    // ------------------------------------------------------------------

    [Fact]
    public void FormB_CalleeMutationVisibleToCaller()
    {
        var list = new List<int> { 1, 2 };
        ParamsLib.MutateBatch(list);
        Assert.Equal(3, list.Count);
        Assert.Equal(99, list[2]);
    }

    [Fact]
    public void FormB_SpanWriteVisibleToCallerArray()
    {
        var buffer = new int[] { 7 };
        ParamsLib.ZeroFirst(buffer);
        Assert.Equal(0, buffer[0]);
    }

    // ------------------------------------------------------------------
    // Span writes on the comma-expansion backing array.
    // ------------------------------------------------------------------

    [Fact]
    public void SpanCommaExpansion_CalleeCanWriteTheFreshArray()
    {
        var result = ParamsLib.CopyAndZero(5, 7);
        Assert.Equal(0, result[0]);
        Assert.Equal(7, result[1]);
    }

    // ------------------------------------------------------------------
    // Side-effect order: expansion elements evaluate left-to-right, once each.
    // ------------------------------------------------------------------

    [Fact]
    public void SideEffectOrder_LeftToRight_OnceEach()
    {
        ParamsLib.ResetTrace();
        ParamsLib.SumAll(ParamsLib.TraceValue(1), ParamsLib.TraceValue(2), ParamsLib.TraceValue(3));
        Assert.Equal(new[] { 1, 2, 3 }, ParamsLib.Trace);

        ParamsLib.ResetTrace();
        ParamsLib.Sum(ParamsLib.TraceValue(4), ParamsLib.TraceValue(5));
        Assert.Equal(new[] { 4, 5 }, ParamsLib.Trace);
    }

    // ------------------------------------------------------------------
    // Virtual family: override dispatch, interface (implicit + explicit),
    // base constructor chain with a params expansion.
    // ------------------------------------------------------------------

    [Fact]
    public void Hierarchy_OverrideDispatch_AndBaseCall()
    {
        // Virtual dispatch reaches the override both through the derived and
        // the base-typed reference; the base implementation is only reached
        // through base.Emit(parts) inside the override (form B passthrough).
        Assert.Equal("console:a,b", new ConsoleSink().Emit("a", "b"));
        Assert.Equal("console:x,y", ((FormatSinkBase)new ConsoleSink()).Emit("x", "y"));
    }

    [Fact]
    public void Hierarchy_InterfaceImplicitAndExplicitImplementation()
    {
        Assert.Equal("x+y", ((IBatchSink)new MemoryBatchSink()).Merge("x", "y"));
    }

    [Fact]
    public void Hierarchy_AbstractMember_CharElements()
    {
        Assert.Equal("ab", ((FormatSinkBase)new ConsoleSink()).Flush('a', 'b'));
    }

    [Fact]
    public void Hierarchy_BaseCtorChain_ReceivesExpandedSeeds()
    {
        Assert.Equal("5,5,5", new ConsoleSink(5).ReceivedSeeds);
    }

    // ------------------------------------------------------------------
    // K1 classic + generic varargs shapes stay comma-separated (D8 off).
    // ------------------------------------------------------------------

    [Fact]
    public void ClassicArrayVarargs_ShapePreserved()
    {
        Assert.Equal(0, ParamsLib.Sum(1, 2, 3) - 6);
        Assert.Equal("1,2,3", ParamsLib.JoinAll(1, 2, 3));
        Assert.Equal("a,b", ParamsLib.JoinAll("a", "b"));
    }

    // ------------------------------------------------------------------
    // K3 (M2): builder-backed params collections — expansion, empty state
    // (fresh builder result per call), form B identity, PC-7 forwarded
    // empty slot, generic arity and the KeyValuePair span factory.
    // ------------------------------------------------------------------

    [Fact]
    public void Builder_CustomFactory_ExpansionAndEmpty()
    {
        Assert.Equal(3, ParamsLib.BagCount(1, 2, 3));
        Assert.Equal(0, ParamsLib.BagCount());

        var first = ParamsLib.LastBag;
        ParamsLib.BagCount();
        Assert.NotNull(first);
        Assert.False(ReferenceEquals(first, ParamsLib.LastBag),
            "empty builder-backed call must build a fresh instance per call");

        // The forwarded overload fills the empty params slot with the same
        // builder empty state.
        Assert.Equal(0, ParamsLib.BagForwarded());
    }

    [Fact]
    public void Builder_FormB_KeepsCallerReference()
    {
        var bag = new ParamsBag(new[] { 9 });
        Assert.Equal(1, ParamsLib.BagCount(bag));
        Assert.Same(bag, ParamsLib.LastBag);
    }

    [Fact]
    public void Builder_GenericArity_IntAndReference()
    {
        Assert.Equal(2, ParamsLib.ImmCount<int>(1, 2));
        Assert.Equal(2, ParamsLib.ImmCount<string>("a", "b"));
        Assert.Equal(0, ParamsLib.ImmCount<int>());
    }

    [Fact]
    public void Builder_KeyValuePairSpanFactory()
    {
        Assert.Equal(2, ParamsLib.MapCount(
            new KeyValuePair<string, int>("a", 1),
            new KeyValuePair<string, int>("b", 2)));
        Assert.Equal(0, ParamsLib.MapCount());

        var map = new ParamsMap(new[] { new KeyValuePair<string, int>("k", 7) });
        Assert.Equal(1, ParamsLib.MapCount(map));
    }

    [Fact]
    public void GenericTiers_IntStructReference_AllShapes()
    {
        Assert.Equal(3, ParamsLib.TierList(1, 2, 3));
        Assert.Equal(2, ParamsLib.TierList(new TierPoint { X = 1 }, new TierPoint { X = 2 }));
        Assert.Equal(2, ParamsLib.TierList("x", "y"));

        Assert.Equal(3, ParamsLib.TierSpan(1, 2, 3));
        Assert.Equal(1, ParamsLib.TierSpan(new TierPoint { X = 9 }));
        Assert.Equal(2, ParamsLib.TierSpan("p", "q"));

        Assert.Equal(3, ParamsLib.TierImm<int>(1, 2, 3));
        Assert.Equal(1, ParamsLib.TierImm(new TierPoint { X = 5 }));
        Assert.Equal(2, ParamsLib.TierImm<string>("m", "n"));
    }
}
