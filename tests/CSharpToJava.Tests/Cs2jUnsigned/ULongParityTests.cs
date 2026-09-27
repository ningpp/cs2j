using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class ULongParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0L, (long)ULong.MinValue);
        Assert.Equal(-1L, (long)ULong.MaxValue);
        Assert.Equal(18446744073709551615UL, (ulong)(long)ULong.MaxValue);
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt64()
    {
        var rnd = new System.Random(6311);
        for (int i = 0; i < 5000; i++)
        {
            // 用两个随机 int 拼成完整 64 位位模式（含高位置 1），不能只取非负高半。
            long x = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            long y = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            int n = rnd.Next(-130, 140);
            ulong a = (ulong)x;
            ulong b = (ulong)y;
            ULong ua = new ULong(x);
            ULong ub = new ULong(y);
            Assert.Equal(unchecked(a + b), (ulong)(long)(ua + ub));
            Assert.Equal(unchecked(a - b), (ulong)(long)(ua - ub));
            Assert.Equal(unchecked(a * b), (ulong)(long)(ua * ub));
            if (b != 0UL)
            {
                Assert.Equal(a / b, (ulong)(long)(ua / ub));
                Assert.Equal(a % b, (ulong)(long)(ua % ub));
            }
            Assert.Equal(a & b, (ulong)(long)(ua & ub));
            Assert.Equal(a | b, (ulong)(long)(ua | ub));
            Assert.Equal(a ^ b, (ulong)(long)(ua ^ ub));
            Assert.Equal(~a, (ulong)(long)~ua);
            Assert.Equal(a << n, (ulong)(long)(ua << n));
            Assert.Equal(a >> n, (ulong)(long)(ua >> n));
            Assert.Equal(a >>> n, (ulong)(long)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new ULong(1) / new ULong(0));
        Assert.Equal(1UL, (ulong)(long)(new ULong(-1L) / new ULong(-1L)));
        Assert.Equal(0UL, (ulong)(long)(new ULong(-2L) / new ULong(-1L)));
        Assert.Equal(9223372036854775807UL, (ulong)(long)(new ULong(-1L) / new ULong(2L)));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(6312);
        for (int i = 0; i < 5000; i++)
        {
            long x = unchecked((long)(((ulong)(uint)rnd.Next() << 32) | (uint)rnd.Next()));
            ulong a = (ulong)x;
            ULong ua = new ULong(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((ulong)(sbyte)x, (ulong)(long)(ULong)(sbyte)x);
            Assert.Equal((ulong)(short)x, (ulong)(long)(ULong)(short)x);
            Assert.Equal((ulong)(int)x, (ulong)(long)(ULong)(int)x);
            Assert.Equal((ulong)x, (ulong)(long)(ULong)x);
            Assert.Equal((ulong)(byte)x, (ulong)(long)(ULong)new UByte(x));
            Assert.Equal((ulong)(ushort)x, (ulong)(long)(ULong)new UShort(x));
            Assert.Equal((ulong)(uint)x, (ulong)(long)(ULong)new UInt(x));
            Assert.Equal((uint)a, (uint)(UInt)ua);
        }
    }

    [Fact]
    public void FloatDecimalAndBoundaryConversions()
    {
        Assert.Equal(18446744073709551615.0.ToString("R"), new ULong(-1L).ToDouble().ToString("R"));
        Assert.Equal(9223372036854775809.0.ToString("R"), new ULong(unchecked((long)0x8000000000000001)).ToDouble().ToString("R"));
        Assert.Equal(18446744073709551615m, new ULong(-1L).ToDecimal());
        Assert.Equal(9223372036854775808m, new ULong(unchecked((long)0x8000000000000000)).ToDecimal());
        Assert.Equal(0L, (long)(ULong)double.NaN);
        Assert.Equal(-1L, (long)(ULong)double.PositiveInfinity);
        Assert.Equal(unchecked((long)0x8000000000000000), (long)(ULong)9223372036854775808.0);
        Assert.Equal((ulong)5m, (ulong)(long)(ULong)5m);
        Assert.Throws<System.OverflowException>(() => (ULong)(-1m));
        Assert.Throws<System.OverflowException>(() => (ULong)18446744073709551616m);
        Assert.Throws<System.OverflowException>(() => (ULong)1.5m);
    }
}
