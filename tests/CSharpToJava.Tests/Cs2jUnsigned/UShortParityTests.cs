using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UShortParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0, (int)UShort.MinValue);
        Assert.Equal(65535, (int)UShort.MaxValue);
        Assert.Equal(unchecked((short)-1), new UShort(65535).Bits);
        Assert.Equal(0, (int)new UShort(65536));
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeUInt16()
    {
        var rnd = new System.Random(7411);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            ushort a = (ushort)x;
            ushort b = (ushort)y;
            UShort ua = new UShort(x);
            UShort ub = new UShort(y);
            Assert.Equal((int)unchecked((ushort)(a + b)), (int)(ua + ub));
            Assert.Equal((int)unchecked((ushort)(a - b)), (int)(ua - ub));
            Assert.Equal((int)unchecked((ushort)(a * b)), (int)(ua * ub));
            if (b != 0)
            {
                Assert.Equal((int)(a / b), (int)(ua / ub));
                Assert.Equal((int)(a % b), (int)(ua % ub));
            }
            Assert.Equal((int)(a & b), (int)(ua & ub));
            Assert.Equal((int)(a | b), (int)(ua | ub));
            Assert.Equal((int)(a ^ b), (int)(ua ^ ub));
            // ushort 的 ~ 会提升为 int，oracle 侧必须显式截回 ushort 再比较。
            Assert.Equal((int)unchecked((ushort)~a), (int)~ua);
            Assert.Equal((int)unchecked((ushort)(a << n)), (int)(ua << n));
            Assert.Equal((int)(a >> n), (int)(ua >> n));
            Assert.Equal((int)(a >>> n), (int)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UShort(1) / new UShort(0));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(7412);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            ushort a = (ushort)x;
            UShort ua = new UShort(x);
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((int)(ushort)(sbyte)x, (int)(UShort)(sbyte)x);
            Assert.Equal((int)(ushort)(short)x, (int)(UShort)(short)x);
            Assert.Equal((int)(ushort)x, (int)(UShort)x);
            Assert.Equal((int)(ushort)(long)(x - 90000), (int)(UShort)(long)(x - 90000));
            Assert.Equal((int)(byte)a, (int)(UByte)ua);
            Assert.Equal((long)(uint)a, (long)(UInt)ua);
            Assert.Equal((long)(ulong)a, (long)(ULong)ua);
        }
        Assert.Equal(0, (int)(UShort)new UByte(0));
        Assert.Equal(255, (int)(UShort)new UByte(255));
        Assert.Equal(0, (int)(UShort)new UInt(65536));
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((int)(ushort)60000.9, (int)(UShort)60000.9);
        Assert.Equal(0, (int)(UShort)double.NaN);
        Assert.Equal(65535, (int)(UShort)double.PositiveInfinity);
        Assert.Equal((double)(ushort)60000, new UShort(60000).ToDouble());
        Assert.Equal((decimal)(ushort)60000, new UShort(60000).ToDecimal());
        Assert.Equal((int)(ushort)5m, (int)(UShort)5m);
        Assert.Throws<System.OverflowException>(() => (UShort)65536m);
        Assert.Throws<System.OverflowException>(() => (UShort)1.5m);
    }
}
