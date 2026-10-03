using System;
using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// C50 field 上下文关键字行为闸门：在原始树（field 关键字保留）与 B 档中间树
/// （归一为显式后备字段 + get_X/set_X，使用点同步改写）上必须同样通过。
/// </summary>
public class FieldKeywordBehaviorTests
{
    [Fact]
    public void Inpc_initializer_moves_to_backing_and_guard_fires_once()
    {
        var m = new FieldKeywordInpc();
        Assert.Equal("init", m.Name);

        var notifications = 0;
        string? last = null;
        m.Changed += name => { notifications++; last = name; };

        m.Name = "init"; // 相同值：不通知
        Assert.Equal(0, notifications);

        m.Name = "ada";
        Assert.Equal("ada", m.Name);
        Assert.Equal(1, notifications);
        Assert.Equal("Name", last);

        m.Name = "grace";
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void Lazy_coalesce_assigns_backing_on_first_access_only()
    {
        var l = new FieldKeywordLazy();
        Assert.Equal(0, l.LoadCount);

        Assert.Equal("loaded-1", l.Expensive);
        Assert.Equal(1, l.LoadCount);

        Assert.Equal("loaded-1", l.Expensive);
        Assert.Equal(1, l.LoadCount);
    }

    [Fact]
    public void Validating_setter_rejects_out_of_range_and_keeps_backing()
    {
        var v = new FieldKeywordValidating { Age = 30 };
        Assert.Equal(30, v.Age);

        Assert.Throws<ArgumentOutOfRangeException>(() => v.Age = -1);
        Assert.Equal(30, v.Age);
    }

    [Fact]
    public void Get_only_initializer_and_set_only_observable_side_effect()
    {
        var k = new FieldKeywordAccessKinds();
        Assert.Equal(42, k.Doubled);

        int observed = -1;
        k.WriteObserved += v => observed = v;
        k.WriteOnly = 7;
        Assert.Equal(7, observed);
    }

    [Fact]
    public void Static_field_property_read_write_and_use_site()
    {
        var before = FieldKeywordStaticHolder.Counter;
        FieldKeywordStaticHolder.Bump();
        Assert.Equal(before + 1, FieldKeywordStaticHolder.Counter);
    }

    [Fact]
    public void Override_field_property_dispatches_polymorphically_with_separate_state()
    {
        FieldKeywordVirtualBase @base = new FieldKeywordVirtualBase();
        FieldKeywordVirtualBase derived = new FieldKeywordOverrideSample();

        Assert.Equal(1, @base.Kind);
        Assert.Equal(2, derived.Kind);

        derived.Kind = 9;
        Assert.Equal(9, derived.Kind);
        Assert.Equal(1, @base.Kind);
    }

    [Fact]
    public void Readonly_struct_field_property_constructor_assignment()
    {
        var p = new FieldKeywordReadonlyPoint(21);
        Assert.Equal(42, p.Doubled);
    }

    [Fact]
    public void Serialized_dto_round_trips_and_field_attribute_shape_compiles()
    {
        var dto = new FieldKeywordSerializationDto { Id = 11, Secret = "open" };
        Assert.Equal(11, dto.Id);
        Assert.Equal("open", dto.Secret);

        // 未设置时取字段初始化器迁移后的默认值。
        Assert.Equal("shh", new FieldKeywordSerializationDto().Secret);
    }

    [Fact]
    public void Initializer_bypasses_virtual_setter_then_setter_counts()
    {
        var s = new FieldKeywordInitializerSideEffect();
        Assert.Equal(10, s.Weight);
        Assert.Equal(0, s.SetterHits); // 初始化器直写后备字段，不经 setter

        s.Weight = 20;
        Assert.Equal(20, s.Weight);
        Assert.Equal(1, s.SetterHits);
    }
}
