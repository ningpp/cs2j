using System;

namespace SampleMemberDesugarLib;

// C27 record with 去糖样本：member-desugar 在 records.enabled=true 时将 with 表达式
// 改写为虚克隆工厂 + set_X 调用，位置 record reshape 为字段+get/set 方法；关闭时原样保留。
// 行为闸门 SampleMemberDesugarTests/RecordWithBehaviorTests 在两棵树上同样通过。
//
// 覆盖档：位置 record class / 显式成员 record / record 多态继承链 / readonly record struct /
// 无体 record / 空初始化器 / 表达式体 with / 嵌套 with / 元组值拷贝 / required record /
// 不可提升上下文（字段初始化器）整 plan 跳过保留原生 with。

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

/// <summary>required 成员：reshape 时剥离 required（CS2J5215 Info 留痕），with 走克隆工厂。</summary>
public record RequiredNote(string Id)
{
    public required string Note;
}

/// <summary>跳过形态：with 位于字段初始化器（不可提升上下文），整 plan 原子保守不改，
/// 中间树保留原生 with（仍为合法 C#），A/B 两档行为一致。</summary>
public record SkipCarrier
{
    public int X { get; set; }

    public static readonly SkipCarrier Seed = new SkipCarrier { X = 0 };

    // 静态字段初始化器位 with：不可提升，原生 with 保留并由 C# 自身求值。
    public static readonly int SeededX = (Seed with { X = 1 }).X;

    public static int Probe() => SeededX;
}

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

    /// <summary>空初始化器：class 空 with 不提升，箭头体内联克隆工厂调用。</summary>
    public static PositionPoint Reclone(PositionPoint p) => p with { };

    /// <summary>元组值类型 with：按值拷贝 + 成分直写，克隆局部显式闭合元组类型。</summary>
    public static (int A, string B) Retag((int A, string B) t)
        => t with { Item1 = t.A + 1 };

    /// <summary>required record 的 with：required 剥离后工厂克隆仍复制全部状态。</summary>
    public static RequiredNote ReNote(RequiredNote n, string note)
        => n with { Note = note };
}
