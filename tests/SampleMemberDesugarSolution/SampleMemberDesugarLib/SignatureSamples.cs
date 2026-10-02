using System.Collections.Generic;

namespace SampleMemberDesugarLib;

// C46 签名场景（设计 §7.2）：显式接口实现 / new 隐藏 / 协变返回三类。
// 中间树产出可编译 C#；.NET 半区行为由 SampleMemberDesugarTests.SignatureBehaviorTests 守门。

// ---- ① 显式接口实现（无碰撞提升 / 类自有同槽成员让位）----

public interface IShape
{
    string Describe();
}

public class PlainShape : IShape
{
    string IShape.Describe() => "plain";
}

public class CollidingShape : IShape
{
    public string Describe() => "class";
    string IShape.Describe() => "iface";
}

// ---- ② 隐藏成员（实例化石隐藏 / 新虚槽两链）----

public class HidingMethodBase
{
    public string Tag() => "base";
}

public class HidingMethodDerived : HidingMethodBase
{
    public new string Tag() => "derived";
}

public class SlotBase
{
    public virtual string Slot() => "slot-base";
}

public class SlotMid : SlotBase
{
    public new virtual string Slot() => "slot-mid";
}

public class SlotLeaf : SlotMid
{
    public override string Slot() => "slot-leaf";
}

// ---- ③ 协变返回（CR-1 引用直映 / CR-2 泛型擦除桥接 + 更深派生 sub-chain）----

public class CovAnimal
{
}

public class CovDog : CovAnimal
{
}

public class CovBase
{
    public virtual CovAnimal Create() => new CovAnimal();

    public virtual IEnumerable<object> Items() => new List<object> { "base" };
}

public class CovDerived : CovBase
{
    // CR-1：引用类型普通协变，Java 映射后仍是合法子类化 → 直映保留。
    public override CovDog Create() => new CovDog();

    // CR-2：IEnumerable<string> over IEnumerable<object>，Java 擦除后非子类型 → bridge + 改名。
    public override IEnumerable<string> Items() => new List<string> { "derived" };
}

public class CovMore : CovDerived
{
    // 更深派生 override：整组改名，经 bridge 虚分派进入最深改名体。
    public override IEnumerable<string> Items() => new List<string> { "more" };
}