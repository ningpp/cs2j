using System;

namespace SampleMemberDesugarLib;

// C27 record with 去糖样本：member-desugar 在 records.enabled=true 时将 with 表达式
// 改写为虚克隆工厂 + set_X 调用，位置 record reshape 为字段+get/set 方法；关闭时原样保留。
// 行为闸门 SampleMemberDesugarTests/RecordWithBehaviorTests 在两棵树上同样通过。

/// <summary>位置 record：主构造参数→字段+get_X/set_X，with 走克隆工厂。</summary>
public record PositionPoint(int X, int Y, string Label);

/// <summary>显式成员 record：get;set; 属性经 C11 归一为 get_X/set_X，with 走克隆工厂。</summary>
public record RecordExplicitPerson
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

/// <summary>record 继承链：虚工厂保证 with 运行时类型为实际派生类型。</summary>
public record ShapeBase(string Color);

public record Circle(string Color, double Radius) : ShapeBase(Color);

public record Rectangle(string Color, double Width, double Height) : ShapeBase(Color);

/// <summary>record struct：值类型 with 返回新实例。</summary>
public readonly record struct Vec2(double X, double Y);

/// <summary>无体 record（record R; 形式）：工厂注入前补花括号。</summary>
public record NoBodyToken(string Id, long Timestamp);

/// <summary>多态 with 使用点：通过基类引用 with，运行时类型保持。</summary>
public static class RecordWithUsage
{
    public static PositionPoint MovePoint(PositionPoint p, int dx, int dy)
        => p with { X = p.X + dx, Y = p.Y + dy };

    public static RecordExplicitPerson Birthday(RecordExplicitPerson p)
        => p with { Age = p.Age + 1 };

    /// <summary>通过基类引用 with，虚工厂保证返回实际派生类型。</summary>
    public static ShapeBase Recolor(ShapeBase s, string color)
        => s with { Color = color };

    public static Vec2 Add(Vec2 a, Vec2 b)
        => a with { X = a.X + b.X, Y = a.Y + b.Y };

    public static NoBodyToken Refresh(NoBodyToken t)
        => t with { Timestamp = t.Timestamp + 1 };

    /// <summary>嵌套 with：内层结果作为外层接收者。</summary>
    public static PositionPoint NestedWith(PositionPoint p)
        => (p with { X = 10 }) with { Y = 20 };
}
