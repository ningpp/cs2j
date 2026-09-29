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
        var sample = new ExpressionTreeSample { Erp = 42 };
        Assert.Equal(84, sample.Selector.Compile()());
        Assert.Equal("shared", StaticInitializerSample.Shared.Label);
    }

    [Fact]
    public void Indexers_behave_identically()
    {
        var grid = new Grid();
        grid[1, 2] = 9;
        Assert.Equal(9, grid[1, 2]);
        grid[1, 1] = 4;
        Assert.Equal(4, grid.Sum()); // Sum 只读对角线 [0,0]+[1,1]+[2,2]

        var named = new NamedIndexer();
        named[3] = "abc";
        Assert.Equal("abc", named[3]);

        var eg = new ExplicitGrid();
        IRow row = eg;
        IGrid g2 = eg;
        g2[0, 1] = 5;
        Assert.Equal(5, row[1]);

        Assert.Equal(13, new RefCells().Bump());

        var se = new SideEffectIndexer();
        se.Bump();
        Assert.Equal(1, GetSeed(se));
    }

    [Fact]
    public void Indexers_single_evaluation_of_side_effecting_argument()
    {
        var se = new SideEffectIndexer();
        se.Bump(); // this[Next()] += 10 —— Next() 必须只调用一次
        Assert.Equal(1, GetSeed(se));
    }

    private static int GetSeed(SideEffectIndexer se)
        => (int)typeof(SideEffectIndexer).GetField("_seed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(se)!;

    // ---- C13 事件行为闸门：订阅顺序 / 退订 / 零订阅不抛 / 直接触发 NRE / custom 转发 / 显式接口 ----

    [Fact]
    public void Events_fire_in_subscription_order_and_unsubscribe_stops_delivery()
    {
        var calls = new List<string>();
        var source = new ImplicitEventSource();
        EventHandler h1 = (_, _) => calls.Add("h1");
        EventHandler h2 = (_, _) => calls.Add("h2");
        source.Changed += h1;
        source.Changed += h2;
        source.Changed += h1; // 重复订阅 → 触发两次
        source.Raise();
        Assert.Equal(new[] { "h1", "h2", "h1" }, calls);
        source.Changed -= h1; // Delegate.Remove 移除最后一段匹配调用表：[h1,h2,h1] → [h1,h2]
        calls.Clear();
        source.Raise();
        Assert.Equal(new[] { "h1", "h2" }, calls);
    }

    [Fact]
    public void Event_guarded_invoke_is_noop_without_subscribers()
    {
        var sample = new EventSamples();
        var fired = false;
        sample.First += () => fired = true;
        sample.Raise("nobody-listens-to-basic");
        Assert.True(fired); // 已订阅者仍被触发；未订阅的 Basic?.Invoke 为无操作
    }

    [Fact]
    public void Event_direct_invoke_throws_nre_when_no_subscribers()
    {
        var sample = new EventSamples();
        Assert.Throws<NullReferenceException>(sample.RaiseDirectNoSubscribers);
    }

    [Fact]
    public void Custom_forwarded_event_and_explicit_interface_dispatch()
    {
        var calls = new List<string>();
        var custom = new CustomEventSample();
        Action handler = () => calls.Add("custom");
        custom.Forwarded += handler;
        custom.RaiseForwarded();
        Assert.Equal(new[] { "custom" }, calls);
        custom.Forwarded -= handler;
        calls.Clear();
        custom.RaiseForwarded();
        Assert.Empty(calls);

        IEventSource source = new ExplicitEventSource();
        EventHandler eh = (_, _) => calls.Add("explicit");
        source.Changed += eh;
        ((ExplicitEventSource)source).RaiseExplicit();
        Assert.Equal(new[] { "explicit" }, calls);
        source.Changed -= eh;
        ((ExplicitEventSource)source).RaiseExplicit();
        Assert.Equal(new[] { "explicit" }, calls);
    }

    [Fact]
    public void Virtual_and_static_and_struct_events_dispatch()
    {
        var calls = new List<string>();
        EventDerived derived = new();
        derived.Virtual += (_, _) => calls.Add("virtual");
        derived.RaiseDerived();
        derived.RaiseBase(); // field-style override 自带新后备字段：base.RaiseBase 触发 base 字段 → 无新增
        Assert.Equal(new[] { "virtual" }, calls);

        var ticked = 0;
        StaticEventHolder.Ticked += () => ticked++;
        StaticEventHolder.Tick();
        StaticEventHolder.Tick();
        Assert.Equal(2, ticked);

        var structCalls = 0;
        var holder = new StructEventHolder();
        holder.Changed += () => structCalls++;
        holder.Raise();
        Assert.Equal(1, structCalls);
    }
}
