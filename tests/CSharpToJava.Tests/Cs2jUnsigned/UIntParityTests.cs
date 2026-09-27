using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UIntParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0L, (long)UInt.MinValue);
        Assert.Equal(4294967295L, (long)UInt.MaxValue);
        Assert.Equal(-1, new UInt(4294967295L).Bits);
        Assert.Equal(0, new UInt(4294967296L).Bits);
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt32()
    {
        var rnd = new System.Random(5311);
        for (int i = 0; i < 4000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            uint a = (uint)x;
            uint b = (uint)y;
            UInt ua = new UInt(x);
            UInt ub = new UInt(y);
            Assert.Equal((long)unchecked(a + b), (long)(ua + ub));
            Assert.Equal((long)unchecked(a - b), (long)(ua - ub));
            Assert.Equal((long)unchecked(a * b), (long)(ua * ub));
            if (b != 0)
            {
                Assert.Equal((long)(a / b), (long)(ua / ub));
                Assert.Equal((long)(a % b), (long)(ua % ub));
            }
            Assert.Equal((long)(a & b), (long)(ua & ub));
            Assert.Equal((long)(a | b), (long)(ua | ub));
            Assert.Equal((long)(a ^ b), (long)(ua ^ ub));
            Assert.Equal((long)~a, (long)~ua);
            Assert.Equal((long)(a << n), (long)(ua << n));
            Assert.Equal((long)(a >> n), (long)(ua >> n));
            Assert.Equal((long)(a >>> n), (long)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UInt(1) / new UInt(0));
        Assert.Equal(2147483648L, (long)(new UInt(unchecked((int)0x80000000)) / new UInt(1)));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(5312);
        for (int i = 0; i < 4000; i++)
        {
            int x = rnd.Next();
            uint a = (uint)x;
            UInt ua = new UInt(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((uint)(sbyte)x, (uint)(UInt)(sbyte)x);
            Assert.Equal((uint)(short)x, (uint)(UInt)(short)x);
            Assert.Equal((uint)x, (uint)(UInt)x);
            long lv = (long)x - 3000000000L;
            Assert.Equal((uint)lv, (uint)(UInt)lv);
            Assert.Equal((uint)(byte)x, (uint)(UInt)new UByte(x));
            Assert.Equal((uint)(ushort)x, (uint)(UInt)new UShort(x));
            Assert.Equal((int)(byte)a, (int)(UByte)ua);
            Assert.Equal((int)(ushort)a, (int)(UShort)ua);
            Assert.Equal((ulong)a, (ulong)(long)(ULong)ua);
            Assert.Equal((uint)(ulong)(a + 4294967296UL), (uint)(UInt)new ULong((long)(a + 4294967296UL)));
        }
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((long)(uint)3000000000.9, (long)(UInt)3000000000.9);
        Assert.Equal(0L, (long)(UInt)double.NaN);
        Assert.Equal(4294967295L, (long)(UInt)double.PositiveInfinity);
        Assert.Equal((double)(uint)3000000000, new UInt(3000000000).ToDouble());
        Assert.Equal((float)(uint)3000000000, new UInt(3000000000).ToSingle());
        Assert.Equal((decimal)(uint)3000000000, new UInt(3000000000).ToDecimal());
        Assert.Equal((long)(uint)5m, (long)(UInt)5m);
        Assert.Throws<System.OverflowException>(() => (UInt)4294967296m);
        Assert.Throws<System.OverflowException>(() => (UInt)1.5m);
    }
}
