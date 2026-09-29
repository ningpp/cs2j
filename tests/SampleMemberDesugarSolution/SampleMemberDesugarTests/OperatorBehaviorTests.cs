using SampleMemberDesugarLib;
using Xunit;

namespace SampleMemberDesugarTests;

/// <summary>
/// C14 运算符去糖行为闸门（设计 §7.2/§7.4）：同一测试在原始 fixture 与去糖中间树上
/// 都必须通过 —— 运算符语义在静态方法形态下保持等价（member-desugar verify 双默认开，
/// 中间树 dotnet test 会执行本文件）。
/// </summary>
public class OperatorBehaviorTests
{
    [Fact]
    public void Arithmetic_Composes()
    {
        var p = new Point(1, 2) + new Point(3, 4);
        Assert.Equal(4, p.X);
        Assert.Equal(6, p.Y);
        Assert.Equal(11, new Point(1, 2) * new Point(3, 4)); // 1*3 + 2*4
        Assert.Equal(new Point(2, 4), 2 * new Point(1, 2));
        Assert.Equal(new Point(-2, -2), new Point(1, 2) - new Point(3, 4));
    }

    [Fact]
    public void ComparisonAndBitwise_Survive()
    {
        Assert.True(new Point(1, 1) == new Point(1, 1));
        Assert.True(new Point(1, 1) != new Point(2, 2));
        Assert.True(new Point(1, 1) < new Point(2, 2));
        Assert.True(new Point(2, 2) > new Point(1, 1)); // > 经 b < a 定义
        Assert.Equal(0b1000, (new Flags(0b1100) & new Flags(0b1010)).V);
        Assert.Equal(0b1110, (new Flags(0b1100) | new Flags(0b1010)).V);
        Assert.Equal(0b0110, (new Flags(0b1100) ^ new Flags(0b1010)).V);
        Assert.Equal(0b0011, (~new Flags(unchecked((int)0xFFFFFFFC))).V);
        Assert.Equal(0b0100, (new Flags(0b0001) << 2).V);
        Assert.Equal(0b0010, (new Flags(0b0100) >> 1).V);
        Assert.Equal(-3, (-new Flags(3)).V);
        Assert.False(!new Flags(1));
        Assert.True(!new Flags(0));
    }

    [Fact]
    public void CheckedPair_BothCallable()
    {
        Assert.Equal(3L, (new Wide(1) + new Wide(2)).Lo);
        Assert.Equal(3L, checked(new Wide(1) + new Wide(2)).Lo);
    }

    [Fact]
    public void Conversions_RoundTrip()
    {
        var t = new Temperature(100.0);
        Assert.Equal(100.0, (double)t);
        Assert.Equal(100, (int)t);
        Assert.Equal(100.0, (double)new Temperature(100.0));
    }

    [Fact]
    public void LiftedConversion_NullPropagates()
    {
        Snowflake? s = new Snowflake(7);
        Assert.Equal(7.0, (double?)s);
        Snowflake? none = null;
        Assert.Null((double?)none);
    }

    [Fact]
    public void LiftedArithmeticAndEquality_ThreeValued()
    {
        OptionalNum? x = new OptionalNum(2);
        OptionalNum? y = new OptionalNum(3);
        OptionalNum? z = null;

        Assert.Equal(5, (x + y)!.Value.V);
        Assert.Null(x + z);
        Assert.True(x == x);
        Assert.False(x == z);
        Assert.True(x != z);
        OptionalNum? n1 = null, n2 = null;
        Assert.True(n1 == n2);
    }

    [Fact]
    public void LiftedCompound_PropagatesNull()
    {
        // 提升展开要求运算元为简单形态（局部/参数/字段）：副作用表达式走 5176 原子跳过
        var v = new OptionalNum(3);
        OptionalNum? x = new OptionalNum(2);
        x += v;
        Assert.Equal(5, x!.Value.V);
        OptionalNum? z = null;
        z += v;
        Assert.Null(z);
    }

    [Fact]
    public void CompoundAssignment_LocalTarget()
    {
        var m = new Money(1);
        m += new Money(2);
        Assert.Equal(3m, m.Amount);
        var f = new Flags(0b1100);
        f &= new Flags(0b1010);
        Assert.Equal(0b1000, f.V);
    }

    [Fact]
    public void NameCollision_UserMethodAndOperatorCoexist()
    {
        var a = new Money(1);
        var b = new Money(2);
        Assert.Equal(3m, (a + b).Amount);
        Assert.Equal(3m, Money.Add(a, b).Amount);
    }

    [Fact]
    public void GateSemantics_Preserved()
    {
        var t = new SqlBool(1);
        var f = new SqlBool(0);
        Assert.Equal(0, (t & f).V);
        Assert.Equal(1, (t | f).V);
        Assert.Equal(1, (t ^ f).V);
        Assert.Equal(1, (t + f).V);
        if (t) { Assert.True(true); } else { Assert.Fail("op_True lost"); }
        if (f) { Assert.Fail("op_False lost"); } else { Assert.True(true); }
        Assert.True((bool)t);
        Assert.True(t || f);
    }

    [Fact]
    public void Increment_DeferredButWorks()
    {
        // 用户定义 ++ 返回同一实例（引用类型），故后缀形式亦见自增后的值
        var c = new Counter();
        var d = c++;
        Assert.Equal(1, d.V);
        Assert.Equal(1, c.V);
        Assert.Same(c, d);
    }

    [Fact]
    public void GenericOperator_Survives()
    {
        var sum = new Vec<int>(1, 2) + new Vec<int>(3, 4);
        Assert.Equal(1, sum.X);
        Assert.Equal(4, sum.Y);
    }

    [Fact]
    public void ExpressionTree_StillUsable()
    {
        var expr = ExprTreeUser.Add;
        Assert.NotNull(expr);
        Assert.Equal(3, expr!.Compile()(new ExprPoint(1, 1), new ExprPoint(2, 2)).X);
    }

    [Fact]
    public void ReflectionContract_PinsDesugaredShape()
    {
        // §7.4 两态断言：原始树 op_Addition 存在；去糖树 Add 存在（互斥）
        var op = typeof(Point).GetMethod("op_Addition");
        var add = typeof(Point).GetMethod("Add");
        if (op != null) Assert.Null(add);
        else Assert.NotNull(add);

        // 门控运算符两侧都保留（C14 不动，JK/转换器消费）
        Assert.NotNull(typeof(SqlBool).GetMethod("op_BitwiseAnd"));
        Assert.NotNull(typeof(SqlBool).GetMethod("op_True"));

        // 表外目标转换两侧都原样保留
        Assert.NotNull(typeof(Temperature).GetMethod("op_Explicit"));
    }
}
