using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// .NET 半场行为闸门（设计 §7.2）：这些测试在去糖前的基线与去糖后的中间树上
/// 必须同样通过 —— 属性语义在 get_X/set_X 形态下保持等价。
/// </summary>
public class BehaviorTests
{
    [Fact]
    public void Value_semantics_survive_desugaring()
    {
        var line = new OrderLine { Quantity = 3, Price = 2.5m };
        Assert.Equal(7.5m, line.Total);
        line.Quantity += 2;
        Assert.Equal(12.5m, line.Total);
        line.Price++;
        Assert.Equal(17.5m, line.Total);
    }

    [Fact]
    public void Init_and_required_properties_keep_construction_surface()
    {
        var dto = new RequiredDto { Id = 7, Name = "req" };
        Assert.Equal(7, dto.Id);
        Assert.Equal("req", dto.Name);
        Assert.Equal("none", new OrderLine().Note);
    }

    [Fact]
    public void Static_property_read_write()
    {
        var before = StaticHolder.Counter;
        StaticHolder.Bump();
        Assert.Equal(before + 1, StaticHolder.Counter);
    }

    [Fact]
    public void Generic_property_holder()
    {
        var box = new Box<string> { Value = "v" };
        box.Value = box.Value + "!";
        Assert.Equal("v!", box.Value);
    }

    [Fact]
    public void Hiding_properties_dispatch_on_static_type()
    {
        HidingBase hb = new HidingDerived();
        var hd = (HidingDerived)hb;
        Assert.Equal("base", hb.Name);
        Assert.Equal("derived", hd.Name);
    }

    [Fact]
    public void Interface_dim_and_explicit_dispatch()
    {
        var person = new PersonRecord { FirstName = "Grace", LastName = "Hopper" };
        IPerson ip = person;
        Assert.Equal("Grace", ip.FirstName);
        Assert.Equal("Grace Hopper", ip.FullName);

        var ep = new ExplicitPerson();
        Assert.Equal("ada", ((IPerson)ep).FirstName);
        Assert.Equal("lovelace", ((IPerson)ep).LastName);
        Assert.Equal("ada lovelace", ((IPerson)ep).FullName);
    }

    [Fact]
    public void Delegate_typed_property_invocation_and_assignment()
    {
        var d = new DelegateHolder();
        Assert.Equal(6, d.Op(5));
        d.Op = x => x * 2;
        Assert.Equal(10, d.Op(5));
    }

    [Fact]
    public void Accessor_contract_is_method_based()
    {
        // 基线：属性访问器本就以方法形态暴露；去糖后：仅方法形态存在。
        Assert.NotNull(typeof(OrderLine).GetMethod("get_Total"));
        Assert.NotNull(typeof(OrderLine).GetMethod("set_Quantity"));
    }

    [Fact]
    public void Nameof_literalizes_to_declaration_name()
    {
        Assert.Equal("Total", nameof(OrderLine.Total));
        Assert.Equal("FirstName", nameof(IPerson.FirstName));
    }

    [Fact]
    public void Get_only_initializers_preserved()
    {
        var g = new GetOnlyWithInitializer();
        Assert.Equal(3, g.Bits.Count);
        Assert.Equal(new[] { 1, 2, 3 }, g.Bits);
    }

    [Fact]
    public void Backing_field_name_collision_resolves()
    {
        var c = new CollisionSample { Age = 3, Count = 4 };
        Assert.Equal(7, c.Sum);
    }

    [Fact]
    public void Skipped_properties_still_behave()
    {
        Assert.Equal(84, new ExpressionTreeSample { Erp = 42 }.Selector.Compile()());
        Assert.Equal("shared", StaticInitializerSample.Shared.Label);
    }
}
