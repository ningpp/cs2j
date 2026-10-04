using SampleNullConditionalLib;
using Xunit;

namespace SampleNullConditionalTests;

public sealed class NullConditionalRuntimeTests
{
    [Fact]
    public void MemberFallback_BothBranches()
    {
        Assert.Equal(-1, NullConditionalSites.MemberFallback(null));
        Assert.Equal(7, NullConditionalSites.MemberFallback(new Simple { B = 7 }));
    }

    [Fact]
    public void ValueCall_Null_SkipsArguments()
    {
        var c = new CallTarget();
        Assert.Equal(-100, NullConditionalSites.ValueCall(null));
        Assert.Equal(0, c.SideCalls);
    }

    [Fact]
    public void ValueCall_NonNull_EvaluatesArgumentsOnceEach()
    {
        var c = new CallTarget();
        Assert.Equal(12, NullConditionalSites.ValueCall(c));
        Assert.Equal(2, c.SideCalls);
    }

    [Fact]
    public void ArrayIndex_BothBranches()
    {
        Assert.Equal(-1, NullConditionalSites.ArrayIndex(null, 0));
        Assert.Equal(20, NullConditionalSites.ArrayIndex(new[] { 10, 20 }, 1));
    }

    [Fact]
    public void ListIndex_BothBranches()
    {
        Assert.Equal(-2, NullConditionalSites.ListIndex(null, 0));
        Assert.Equal(30, NullConditionalSites.ListIndex(new List<int> { 30, 40 }, 0));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, null)]
    [InlineData(true, 9)]
    public void MultiValue_ChainStates(bool? hasLeaf, int? expected)
    {
        var node = BuildMultiValueNode(hasLeaf);
        Assert.Equal(expected, NullConditionalSites.MultiValue(node));
    }

    private static TreeNode? BuildMultiValueNode(bool? hasLeaf)
    {
        if (hasLeaf is null)
        {
            return null;
        }

        var node = new TreeNode();
        if (hasLeaf.Value)
        {
            var leaf = new Leaf { Value = 9 };
            node.Leaf = leaf;
        }

        return node;
    }

    [Fact]
    public void MultiVoid_FiresOnlyOnFullChain()
    {
        var leaf = new Leaf();
        NullConditionalSites.MultiVoid(null, 1);
        NullConditionalSites.MultiVoid(new TreeNode { Leaf = null }, 1);
        Assert.Equal(0, leaf.FireCalls);

        var node = new TreeNode { Leaf = leaf };
        NullConditionalSites.MultiVoid(node, 1);
        Assert.Equal(1, leaf.FireCalls);
    }

    [Theory]
    [InlineData(false, false, -3)]
    [InlineData(true, false, -3)]
    [InlineData(true, true, 55)]
    public void MixedCall_States(bool root, bool target, int expected)
    {
        var node = BuildMixedNode(root, target);
        Assert.Equal(expected, NullConditionalSites.MixedCall(node));
    }

    private static TreeNode? BuildMixedNode(bool root, bool target)
    {
        if (!root)
        {
            return null;
        }

        var node = new TreeNode();
        if (target)
        {
            var t = new TreeNode();
            var leaf = new Leaf { Value = 55 };
            t.Leaf = leaf;
            node.JumpTarget = t;
        }

        return node;
    }

    [Fact]
    public void Jagged_BothBranches()
    {
        Assert.Null(NullConditionalSites.Jagged(null, 0));
        Assert.Null(NullConditionalSites.Jagged(new int[1][], 0));
        Assert.Equal(2, NullConditionalSites.Jagged(new[] { new[] { 1, 2 } }, 0));
    }

    [Fact]
    public void Invoke_PublicFieldAndEventAndFunc()
    {
        var s = new InvokeSites();
        s.VoidPub(3);
        s.VoidEventNull();
        Assert.Equal(0, s.HandlerCalls);
        Assert.Equal(-9, s.ValueFunc(4));

        s.Pub += s.Handler;
        s.VoidPub(3);
        s.VoidEventSubscribed(5);
        s.Fn = InvokeSitesDoubler;
        Assert.Equal(8, s.ValueFunc(4));
        Assert.Equal(8, s.HandlerCalls);
    }

    private static int InvokeSitesDoubler(int x) => x * 2;
}
