using System;

namespace SampleMemberDesugarLib;

/// <summary>
/// C14 运算符矩阵样本（设计 §7.2）：算术/多重载/比较/位/一元/checked 对/泛型/碰撞。
/// 基线（去糖前）与去糖后行为必须一致（OperatorBehaviorTests 双态运行）。
/// </summary>
public struct Point
{
    public int X;
    public int Y;

    public Point(int x, int y) { X = x; Y = y; }

    public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
    public static Point operator -(Point a, Point b) => new Point(a.X - b.X, a.Y - b.Y);
    public static Point operator *(Point a, int k) => new Point(a.X * k, a.Y * k);
    public static Point operator *(int k, Point a) => new Point(a.X * k, a.Y * k);
    public static double operator *(Point a, Point b) => a.X * b.X + a.Y * b.Y;
    public static bool operator ==(Point a, Point b) => a.X == b.X && a.Y == b.Y;
    public static bool operator !=(Point a, Point b) => !(a == b);
    public static bool operator <(Point a, Point b) => a.X * a.X + a.Y * a.Y < b.X * b.X + b.Y * b.Y;
    public static bool operator >(Point a, Point b) => b < a;

    public override bool Equals(object? o) => o is Point p && p == this;
    public override int GetHashCode() => HashCode.Combine(X, Y);
}

/// <summary>checked 对（C# 11）：两版声明 → Add / AddChecked。</summary>
public struct Wide
{
    public long Lo;

    public Wide(long lo) { Lo = lo; }

    public static Wide operator +(Wide a, Wide b) => new Wide(a.Lo + b.Lo);
    public static Wide operator checked +(Wide a, Wide b) => new Wide(checked(a.Lo + b.Lo));
}

/// <summary>泛型运算符（Vec&lt;T&gt; 声明与站点同形）。</summary>
public struct Vec<T> where T : struct
{
    public T X;
    public T Y;

    public Vec(T x, T y) { X = x; Y = y; }

    public static Vec<T> operator +(Vec<T> a, Vec<T> b) => new Vec<T>(a.X, b.Y);
}

/// <summary>命名碰撞样本：operator + 与手写 Add 共存 → 去糖名升级为 AddOperator。</summary>
public class Money
{
    public decimal Amount;

    public Money(decimal amount) { Amount = amount; }

    public static Money operator +(Money a, Money b) => new Money(a.Amount + b.Amount);
    public static Money Add(Money a, Money b) => new Money(a.Amount + b.Amount);
    public static Money operator -(Money a) => new Money(-a.Amount);
}

/// <summary>位标志与移位/一元（非门控类型：&amp;/| 一并去糖）。</summary>
public struct Flags
{
    public int V;

    public Flags(int v) { V = v; }

    public static Flags operator &(Flags a, Flags b) => new Flags(a.V & b.V);
    public static Flags operator |(Flags a, Flags b) => new Flags(a.V | b.V);
    public static Flags operator ^(Flags a, Flags b) => new Flags(a.V ^ b.V);
    public static Flags operator ~(Flags a) => new Flags(~a.V);
    public static Flags operator <<(Flags a, int n) => new Flags(a.V << n);
    public static Flags operator >>(Flags a, int n) => new Flags(a.V >> n);
    public static Flags operator >>>(Flags a, int n) => new Flags(a.V >>> n);
    public static Flags operator -(Flags a) => new Flags(-a.V);
    public static Flags operator +(Flags a) => a;
    public static bool operator !(Flags a) => a.V == 0;
}
