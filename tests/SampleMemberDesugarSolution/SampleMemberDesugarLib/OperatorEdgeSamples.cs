using System;
using System.Linq.Expressions;

namespace SampleMemberDesugarLib;

/// <summary>
/// C14 边界样本（设计 §7.2）：转换/提升/门控/移交 C17/表外目标/表达式树跳过。
/// </summary>
public struct Temperature
{
    public double C;

    public Temperature(double c) { C = c; }

    public static implicit operator double(Temperature t) => t.C;
    public static implicit operator int(Temperature t) => (int)t.C;
    // 目标为用户类型（命名镜像表外）→ CS2J5177 整 plan 跳过，声明与站点原子不动
    public static explicit operator Temperature(double c) => new Temperature(c);
}

/// <summary>提升转换链样本：Snowflake? → double?（公式 ①）。</summary>
public struct Snowflake
{
    public int V;

    public Snowflake(int v) { V = v; }

    public static implicit operator double(Snowflake s) => s.V;
}

/// <summary>提升算术/相等样本（公式 ③/⑤）。</summary>
public struct OptionalNum
{
    public int V;

    public OptionalNum(int v) { V = v; }

    public static OptionalNum operator +(OptionalNum a, OptionalNum b) => new OptionalNum(a.V + b.V);
    public static bool operator ==(OptionalNum a, OptionalNum b) => a.V == b.V;
    public static bool operator !=(OptionalNum a, OptionalNum b) => a.V != b.V;

    public override bool Equals(object? o) => o is OptionalNum n && n == this;
    public override int GetHashCode() => V;
}

/// <summary>三值门控类型（op_True/op_False + &amp;/| + 算术 + bool 隐式）——JK 组合边界。</summary>
public struct SqlBool
{
    public int V;

    public SqlBool(int v) { V = v; }

    public static SqlBool operator &(SqlBool a, SqlBool b) => new SqlBool(a.V & b.V);
    public static SqlBool operator |(SqlBool a, SqlBool b) => new SqlBool(a.V | b.V);
    public static SqlBool operator ^(SqlBool a, SqlBool b) => new SqlBool(a.V ^ b.V);
    public static SqlBool operator +(SqlBool a, SqlBool b) => new SqlBool(a.V + b.V);
    public static bool operator true(SqlBool x) => x.V != 0;
    public static bool operator false(SqlBool x) => x.V == 0;
    public static implicit operator bool(SqlBool x) => x.V != 0;
}

/// <summary>++/-- 移交 C17（原样保留样本）。</summary>
public class Counter
{
    public int V;

    public static Counter operator ++(Counter c) { c.V++; return c; }
}

/// <summary>表达式树样本（隔离类型）：引用使 + plan 原子跳过（CS2J5173）。</summary>
public struct ExprPoint
{
    public int X;
    public int Y;

    public ExprPoint(int x, int y) { X = x; Y = y; }

    public static ExprPoint operator +(ExprPoint a, ExprPoint b) => new ExprPoint(a.X + b.X, a.Y + b.Y);
}

public static class ExprTreeUser
{
    public static Expression<Func<ExprPoint, ExprPoint, ExprPoint>> Add = (a, b) => a + b;
}
