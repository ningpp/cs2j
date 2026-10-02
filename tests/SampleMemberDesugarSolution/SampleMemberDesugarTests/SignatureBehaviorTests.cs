using System.Collections.Generic;
using System.Linq;
using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// C46 签名场景 .NET 半区预言机（设计 §7.2/§7.3）：接口引用 vs 类引用分派、
/// new 隐藏按静态类型决议、new virtual 两虚槽链、协变两路径返回运行时类型与多态。
/// 这些断言在去糖前的基线与去糖后的中间树上必须同样通过。
/// </summary>
public class SignatureBehaviorTests
{
    [Fact]
    public void Explicit_interface_dispatch_vs_class_reference()
    {
        IShape plain = new PlainShape();
        Assert.Equal("plain", plain.Describe());

        IShape colliding = new CollidingShape();
        Assert.Equal("iface", colliding.Describe());           // 接口槽位体
        Assert.Equal("class", new CollidingShape().Describe()); // 类自有成员
    }

    [Fact]
    public void Hiding_dispatches_on_static_type()
    {
        HidingMethodBase b = new HidingMethodDerived();
        HidingMethodDerived d = new HidingMethodDerived();
        Assert.Equal("base", b.Tag());       // 基类静态类型 → 基槽
        Assert.Equal("derived", d.Tag());    // 派生静态类型 → 隐藏成员（去糖后改名）
    }

    [Fact]
    public void New_virtual_slot_chain_two_independent_dispatches()
    {
        SlotBase sb = new SlotLeaf();
        SlotMid sm = new SlotLeaf();
        SlotLeaf sl = new SlotLeaf();
        Assert.Equal("slot-base", sb.Slot());   // 基槽链
        Assert.Equal("slot-leaf", sm.Slot());   // newslot 链（去糖后整组改名，虚分派仍进入叶）
        Assert.Equal("slot-leaf", sl.Slot());
    }

    [Fact]
    public void Covariant_reference_return_both_paths()
    {
        CovBase b = new CovDerived();
        CovDerived d = new CovDerived();
        Assert.IsType<CovDog>(b.Create());   // 基类静态类型：虚分派
        Assert.IsType<CovDog>(d.Create());   // 派生静态类型：原 override 绑定
    }

    [Fact]
    public void Covariant_erasure_bridge_both_paths_and_multiformity()
    {
        CovBase b = new CovMore();
        CovDerived d = new CovMore();
        CovMore m = new CovMore();

        var viaBase = b.Items();   // 经 bridge 虚分派进入最深改名体
        Assert.Equal("more", viaBase.First());
        Assert.IsAssignableFrom<IEnumerable<string>>(viaBase);

        var viaDerived = d.Items();   // 派生静态类型绑定原 override
        Assert.Equal("more", viaDerived.First());

        var viaMore = m.Items();
        Assert.Equal("more", viaMore.First());
    }
}