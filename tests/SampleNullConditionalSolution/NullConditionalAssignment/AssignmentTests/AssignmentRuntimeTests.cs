using AssignmentLib;
using Xunit;

namespace AssignmentTests;

/// <summary>
/// Runtime behavior of the lowered assignment sites. These tests execute
/// against the preprocessed intermediate tree during the verify step, so
/// they simultaneously prove the lowered output is behavior-identical to
/// native C#.
/// </summary>
public sealed class AssignmentRuntimeTests
{
    [Fact]
    public void NullReceivers_EverySite_NoOps()
    {
        AssignmentSites.Member(null, 1);
        AssignmentSites.Property(null, 1);
        AssignmentSites.Index(null, 0, 1);
        AssignmentSites.Add(null, 1);
        AssignmentSites.EventOn(null, () => { });
        AssignmentSites.Coalesce(null, "x");
        AssignmentSites.Chain(null, 1);
        Assert.Null(AssignmentSites.ValueForm(null, 1));
        Assert.Null(AssignmentSites.CoalesceValue(null, "x"));
    }

    [Fact]
    public void MemberPropertyIndex_NonNull_Assign()
    {
        var node = new Node();

        AssignmentSites.Member(node, 42);
        Assert.Equal(42, node.Field);

        AssignmentSites.Property(node, 7);
        Assert.Equal(7, node.Prop);

        AssignmentSites.Index(node, 0, 9);
        Assert.Equal(9, node.Field);
    }

    [Fact]
    public void CompoundAndEvent_NonNull_ApplyOnce()
    {
        var node = new Node();

        AssignmentSites.Add(node, 5);
        Assert.Equal(5, node.Field);
        AssignmentSites.Add(node, 5);
        Assert.Equal(10, node.Field);

        AssignmentSites.EventOn(node, () => { });
        Assert.Equal(1, node.Adds);
    }

    [Fact]
    public void Coalesce_NullSets_SetKeeps()
    {
        var empty = new Node();
        AssignmentSites.Coalesce(empty, "first");
        Assert.Equal("first", empty.Label);
        AssignmentSites.Coalesce(empty, "second");
        Assert.Equal("first", empty.Label);
    }

    [Fact]
    public void Chain_InnerNullNoop_BothSetAssigns()
    {
        var outerOnly = new Node();
        AssignmentSites.Chain(outerOnly, 5);

        var pair = new Node { Next = new Node() };
        AssignmentSites.Chain(pair, 5);
        Assert.Equal(5, pair.Next.Field);
    }

    [Fact]
    public void ValueForms_ReturnAssignedValue()
    {
        var node = new Node();
        Assert.Equal(3, AssignmentSites.ValueForm(node, 3));
        Assert.Equal(3, node.Field);

        var labelled = new Node();
        Assert.Equal("v", AssignmentSites.CoalesceValue(labelled, "v"));
        Assert.Equal("v", labelled.Label);

        var already = new Node { Label = "kept" };
        Assert.Equal("kept", AssignmentSites.CoalesceValue(already, "new"));
    }

    [Fact]
    public void PlainCoalesce_NoConditionalAccess_StillWorks()
    {
        var node = new Node();
        AssignmentSites.PlainCoalesce(node, "plain");
        Assert.Equal("plain", node.Label);
        AssignmentSites.PlainCoalesce(node, "other");
        Assert.Equal("plain", node.Label);
    }
}
