using SampleExtensionMembers;
using Xunit;

namespace SampleExtensionMembers.Tests;

/// <summary>
/// Runtime semantics proof for the extension-member samples: the methods,
/// properties, operators and static members declared inside extension blocks
/// behave exactly like their ordinary/classic-extension counterparts.
/// </summary>
public sealed class RuntimeBehaviorTests
{
    [Fact]
    public void Box_InstanceMethodAndProperty_UseReceiver()
    {
        var box = new Box<int>(21);
        Assert.Equal("Box<Int32>(21)", box.Describe());
        Assert.Equal(21, box.Peek);
    }

    [Fact]
    public void Box_TwoLevelBlock_FullNAndMTypeArguments()
    {
        Assert.Equal("Box`1 + x", Box<int>.PairWith<Box<int>, string>("x"));
    }

    [Fact]
    public void Vec_Operators_EvaluateNormally()
    {
        var a = new Vec(1, 2);
        var b = new Vec(3, 5);

        var sum = a + b;
        Assert.Equal(4, sum.X);
        Assert.Equal(7, sum.Y);

        var diff = b - a;
        Assert.Equal(2, diff.X);
        Assert.Equal(3, diff.Y);

        var neg = -a;
        Assert.Equal(-1, neg.X);
        Assert.Equal(-2, neg.Y);
    }

    [Fact]
    public void Vec_StaticMembers_QualifiedByReceiverType()
    {
        Assert.Equal("Vec", Vec.Describe());
        Assert.Equal(42, Vec.Unit);
    }

    [Fact]
    public void Num_ScopedRefReceiver_MutatesThroughRef()
    {
        var num = new Num(10);
        num.Bump(5);
        Assert.Equal(15, num.Value);
        Assert.Equal(45, num.Triple);
    }

    [Fact]
    public void ClassicExtensions_CoexistWithBlocks()
    {
        Assert.Equal(9, 3.Square());
        Assert.Equal("HI!", "hi".Shout());
    }
}
