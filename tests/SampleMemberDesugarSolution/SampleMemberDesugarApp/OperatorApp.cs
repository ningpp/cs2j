using SampleMemberDesugarLib;

namespace SampleMemberDesugarApp;

/// <summary>
/// C14 跨文件站点样本（设计 §7.2）：声明在 Lib、站点在 App，plan 的 TouchedFiles 必须覆盖两侧。
/// </summary>
public static class OperatorApp
{
    // 二元/一元站点
    public static Point Translate(Point p, Point d) => p + d;
    public static Point Back(Point p, Point d) => p - d;
    public static double Dot(Point a, Point b) => a * b;
    public static Point Scale(Point p) => p * 2;
    public static Point ScaleBy(Point p) => 2 * p;
    public static bool Same(Point a, Point b) => a == b;
    public static bool Smaller(Point a, Point b) => a < b;
    public static Flags Mix(Flags a, Flags b) => (a & b) | (a ^ b);
    public static Flags Shift(Flags a) => (a << 2) >> 1;
    public static Flags Neg(Flags a) => -a;
    public static bool Empty(Flags a) => !a;

    // checked 变体按绑定符号选择
    public static Wide Plain(Wide a, Wide b) => a + b;
    public static Wide Ckd(Wide a, Wide b) => checked(a + b);

    // 转换站点（隐式 + 显式 cast 到表内目标）
    public static double Celsius(Temperature t) => t;
    public static double Kelvin(Temperature t) => t + 273.15;
    public static int Rounded(Temperature t) => t;
    public static int RoundedCast(Temperature t) => (int)t;

    // 提升站点（转换 ① / 算术 ③ / 相等 ⑤ / 复合 ⑥）
    public static double? Melted(Snowflake? s) => s;
    public static OptionalNum? SumOrNone(OptionalNum? a, OptionalNum? b) => a + b;
    public static bool? SameOrNone(OptionalNum? a, OptionalNum? b) => a == b;
    public static bool? DiffOrNone(OptionalNum? a, OptionalNum? b) => a != b;

    public static OptionalNum? Bump(OptionalNum? x, OptionalNum v)
    {
        x += v;
        return x;
    }

    // 复合赋值（简单局部目标内联）
    public static Money Combine(Money a, Money b)
    {
        a += b;
        return a;
    }

    public static Flags Mask(Flags a, Flags b)
    {
        a &= b;
        return a;
    }

    // 门控站点：& / | 保留给 JK 通道
    public static SqlBool Gate(SqlBool a, SqlBool b) => a & b;
    public static SqlBool GateOr(SqlBool a, SqlBool b) => a | b;
    public static SqlBool GateXor(SqlBool a, SqlBool b) => a ^ b;
    public static SqlBool GateSum(SqlBool a, SqlBool b) => a + b;
    public static bool AsBool(SqlBool x) => x;
    public static bool Guarded(SqlBool a, SqlBool b) => a && b;

    // 泛型运算符站点
    public static Vec<int> Sum(Vec<int> a, Vec<int> b) => a + b;

    // 移交 C17：++ 原样
    public static int BumpCounter(Counter c)
    {
        var d = c++;
        return d.V;
    }
}
