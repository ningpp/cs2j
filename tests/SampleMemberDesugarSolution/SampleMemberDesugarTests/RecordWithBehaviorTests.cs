using System;
using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// C27 record with 行为闸门：在原始树（with 关键字保留）与 B 档中间树
/// （with 改写为虚克隆工厂 + set_X 调用）上必须同样通过。
/// </summary>
public class RecordWithBehaviorTests
{
    [Fact]
    public void Positional_record_with_copies_and_updates_fields()
    {
        var p = new PositionPoint(1, 2, "origin");
        var q = RecordWithUsage.MovePoint(p, 10, 20);

        Assert.Equal(1, p.X);
        Assert.Equal(2, p.Y);
        Assert.Equal("origin", p.Label);

        Assert.Equal(11, q.X);
        Assert.Equal(22, q.Y);
        Assert.Equal("origin", q.Label); // 未赋值字段保持原值
    }

    [Fact]
    public void Explicit_member_record_with_keeps_other_members()
    {
        var p = new RecordExplicitPerson { Name = "ada", Age = 30 };
        var q = RecordWithUsage.Birthday(p);

        Assert.Equal("ada", p.Name);
        Assert.Equal(30, p.Age);

        Assert.Equal("ada", q.Name); // Name 未变
        Assert.Equal(31, q.Age);
    }

    [Fact]
    public void Polymorphic_with_preserves_runtime_type()
    {
        ShapeBase c = new Circle("red", 5.0);
        ShapeBase r = new Rectangle("blue", 3.0, 4.0);

        var c2 = RecordWithUsage.Recolor(c, "green");
        var r2 = RecordWithUsage.Recolor(r, "yellow");

        Assert.IsType<Circle>(c2);
        Assert.Equal("green", c2.Color);
        Assert.Equal(5.0, ((Circle)c2).Radius);

        Assert.IsType<Rectangle>(r2);
        Assert.Equal("yellow", r2.Color);
        Assert.Equal(3.0, ((Rectangle)r2).Width);
        Assert.Equal(4.0, ((Rectangle)r2).Height);
    }

    [Fact]
    public void Record_struct_with_returns_new_value()
    {
        var a = new Vec2(1.0, 2.0);
        var b = new Vec2(3.0, 4.0);
        var sum = RecordWithUsage.Add(a, b);

        Assert.Equal(4.0, sum.X);
        Assert.Equal(6.0, sum.Y);

        // 原值不变
        Assert.Equal(1.0, a.X);
        Assert.Equal(2.0, a.Y);
    }

    [Fact]
    public void No_body_record_with_works()
    {
        var t = new NoBodyToken("abc", 100);
        var t2 = RecordWithUsage.Refresh(t);

        Assert.Equal("abc", t.Id);
        Assert.Equal(100, t.Timestamp);

        Assert.Equal("abc", t2.Id);
        Assert.Equal(101, t2.Timestamp);
    }

    [Fact]
    public void Nested_with_applies_both_updates()
    {
        var p = new PositionPoint(1, 2, "p");
        var q = RecordWithUsage.NestedWith(p);

        Assert.Equal(10, q.X);
        Assert.Equal(20, q.Y);
        Assert.Equal("p", q.Label);
    }

    [Fact]
    public void With_produces_distinct_instance()
    {
        var p = new PositionPoint(1, 2, "p");
        var q = p with { X = 5 };

        Assert.NotSame(p, q); // record 是引用类型，with 返回新实例
        Assert.Equal(5, q.X);
        Assert.Equal(2, q.Y);
    }

    [Fact]
    public void Empty_initializer_with_returns_equal_copy()
    {
        var p = new PositionPoint(1, 2, "p");
        var q = RecordWithUsage.Reclone(p);

        Assert.NotSame(p, q); // 空 with 仍经克隆工厂生成独立实例
        Assert.Equal(p.X, q.X);
        Assert.Equal(p.Y, q.Y);
        Assert.Equal(p.Label, q.Label);
    }

    [Fact]
    public void Tuple_with_copies_and_updates_component()
    {
        var t = (A: 1, B: "x");
        var t2 = RecordWithUsage.Retag(t);

        Assert.Equal(2, t2.A);
        Assert.Equal("x", t2.B);

        // 原值不变（值拷贝）
        Assert.Equal(1, t.A);
        Assert.Equal("x", t.B);
    }

    [Fact]
    public void Required_record_with_keeps_other_members()
    {
        var n = new RequiredNote("id-1") { Note = "first" };
        var n2 = RecordWithUsage.ReNote(n, "second");

        Assert.Equal("id-1", n2.Id);
        Assert.Equal("second", n2.Note);

        // 原值不变
        Assert.Equal("first", n.Note);
    }

    [Fact]
    public void Skip_form_in_field_initializer_runs_natively_on_both_tiers()
    {
        // 字段初始化器中的 with 命中 5223 不提升，B 档中间树保留原生 with，行为与 A 档一致。
        Assert.Equal(1, SkipCarrier.Probe());
    }
}
