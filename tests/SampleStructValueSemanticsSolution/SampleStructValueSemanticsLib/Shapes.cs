using System;

namespace SampleStructValueSemanticsLib;

public interface IMovable
{
    void Translate(int dx, int dy);
}

/// <summary>设计 §7.3 基准可变 struct：public 字段 + 变异方法 + 接口实现。</summary>
public struct Point : IMovable
{
    public int X;
    public int Y;

    public Point(int x, int y)
    {
        X = x;
        Y = y;
    }

    public void Translate(int dx, int dy)
    {
        X += dx;
        Y += dy;
    }

    /// <summary>查询方法：rvalue 接收者也不应触发 VS-3 提升。</summary>
    public int Peek() => X + Y;
}

/// <summary>嵌套可变 struct 字段：拷贝构造需对激活 plan 的字段递归 Clone。</summary>
public struct Line
{
    public Point Start;
    public Point End;

    public Line(Point start, Point end)
    {
        Start = start;
        End = end;
    }

    public Point Midpoint => new((Start.X + End.X) / 2, (Start.Y + End.Y) / 2);

    public void Shift(int dx, int dy)
    {
        Start.Translate(dx, dy);
        End.Translate(dx, dy);
    }
}

/// <summary>struct 数组成员：元素读出/写回站点。</summary>
public struct Polygon
{
    public Point[] Vertices;

    public Polygon(Point[] vertices)
    {
        Vertices = vertices;
    }

    public void ShiftAll(int dx, int dy)
    {
        for (var i = 0; i < Vertices.Length; i++)
        {
            var current = Vertices[i];
            current.Translate(dx, dy);
            Vertices[i] = current;
        }
    }
}

/// <summary>属性 setter 变异 + with 模式（var copy = this; return copy;）。</summary>
public struct BuildSettings
{
    public string Name { get; set; }
    public int Retries { get; set; }

    public BuildSettings WithRetries(int retries)
    {
        var copy = this;
        copy.Retries = retries;
        return copy;
    }
}

/// <summary>自带合规 Clone()：plan 复用既有 shim（SV010）。</summary>
public struct Reusable
{
    public int V;

    public Reusable Clone() => new Reusable { V = V };
}

public static class Geometry
{
    public static Point Origin => new Point(3, 4);

    /// <summary>in 参接收者上的变异：VS-3 提升（C# 语义本就是丢弃的防御副本）。</summary>
    public static void NudgeIn(in Point p)
    {
        p.Translate(1, 1);
    }

    /// <summary>接口形参：调用点 VS-5b 装箱站点。</summary>
    public static void Move(IMovable movable)
    {
        movable.Translate(2, 2);
    }

    /// <summary>in 参装箱到 object：VS-5a 站点（装箱点克隆，非调用点）。</summary>
    public static object Box(in Point p)
    {
        object boxed = p;
        return boxed;
    }

    /// <summary>拆箱：VS-5c 站点。</summary>
    public static Point Unbox(object boxed)
    {
        return (Point)boxed;
    }

    /// <summary>变异值参：调用点必须插 clone。</summary>
    public static void MutateCopy(Point p)
    {
        p.Translate(9, 9);
    }

    /// <summary>数组元素读出经局部变异但不写回：语义上不应影响数组。</summary>
    public static void MutateArrayFirst(Point[] points)
    {
        var p = points[0];
        p.Translate(9, 9);
    }
}

/// <summary>readonly 实例字段 + get-only 属性返回 struct：VS-3 rvalue 接收者。</summary>
public sealed class ReadOnlyHolder
{
    private readonly Point _anchor;

    public ReadOnlyHolder(Point anchor)
    {
        _anchor = anchor;
    }

    public Point Anchor => _anchor;

    public void Nudge()
    {
        _anchor.Translate(1, 1);
    }
}
