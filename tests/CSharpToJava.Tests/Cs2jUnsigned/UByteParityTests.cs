using dotnet.System;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UByteParityTests
{
    [Fact]
    public void ConstantsAndStorage()
    {
        Assert.Equal(0, (int)UByte.MinValue);
        Assert.Equal(255, (int)UByte.MaxValue);
        Assert.Equal(200, (int)new UByte(unchecked((sbyte)200)));
        Assert.Equal(0, (int)new UByte(256));
        Assert.Equal(1, (int)new UByte(257L));
    }

    [Fact]
    public void ArithmeticCompare_ParityWithNativeByte()
    {
        var rnd = new System.Random(7311);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            int y = rnd.Next();
            int n = rnd.Next(-70, 80);
            byte a = (byte)x;
            byte b = (byte)y;
            UByte ua = new UByte(x);
            UByte ub = new UByte(y);
            Assert.Equal((int)unchecked((byte)(a + b)), (int)(ua + ub));
            Assert.Equal((int)unchecked((byte)(a - b)), (int)(ua - ub));
            Assert.Equal((int)unchecked((byte)(a * b)), (int)(ua * ub));
            if (b != 0)
            {
                Assert.Equal((int)(a / b), (int)(ua / ub));
                Assert.Equal((int)(a % b), (int)(ua % ub));
            }
            Assert.Equal((int)(a & b), (int)(ua & ub));
            Assert.Equal((int)(a | b), (int)(ua | ub));
            Assert.Equal((int)(a ^ b), (int)(ua ^ ub));
            // byte 的 ~ 会提升为 int，oracle 侧必须显式截回 byte 再比较。
            Assert.Equal((int)unchecked((byte)~a), (int)~ua);
            Assert.Equal((int)unchecked((byte)(a << n)), (int)(ua << n));
            Assert.Equal((int)(a >> n), (int)(ua >> n));
            Assert.Equal((int)(a >>> n), (int)(ua >>> n));
            Assert.Equal(a == b, ua == ub);
            Assert.Equal(a != b, ua != ub);
            Assert.Equal(a < b, ua < ub);
            Assert.Equal(a <= b, ua <= ub);
            Assert.Equal(a > b, ua > ub);
            Assert.Equal(a >= b, ua >= ub);
            Assert.Equal(a.GetHashCode(), ua.GetHashCode());
            Assert.Equal(a.CompareTo(b), ua.CompareTo(ub));
            Assert.Equal(a.Equals((object)b), ua.Equals((object)ub));
        }
        Assert.Throws<System.DivideByZeroException>(() => new UByte(1) / new UByte(0));
        Assert.Equal(1, new UByte(1).CompareTo((object)null));
        Assert.Throws<System.ArgumentException>(() => new UByte(1).CompareTo((object)1));
        Assert.False(new UByte(1).Equals((object)null));
    }

    [Fact]
    public void SignedAndCrossConversions_Parity()
    {
        var rnd = new System.Random(7312);
        for (int i = 0; i < 3000; i++)
        {
            int x = rnd.Next();
            byte a = (byte)x;
            UByte ua = new UByte(x);
            sbyte sb = (sbyte)x;
            short sh = (short)x;
            int iv = x;
            long lv = x - 100000;
            Assert.Equal((sbyte)a, (sbyte)ua);
            Assert.Equal((short)a, (short)ua);
            Assert.Equal((int)a, (int)ua);
            Assert.Equal((long)a, (long)ua);
            Assert.Equal((int)(byte)sb, (int)(UByte)sb);
            Assert.Equal((int)(byte)sh, (int)(UByte)sh);
            Assert.Equal((int)(byte)iv, (int)(UByte)iv);
            Assert.Equal((int)(byte)lv, (int)(UByte)lv);
            Assert.Equal((int)(ushort)a, (int)(UShort)ua);
            Assert.Equal((long)(uint)a, (long)(UInt)ua);
            Assert.Equal((long)(ulong)a, (long)(ULong)ua);
        }
        Assert.Equal(0, (int)(UByte)new UShort(256));
        Assert.Equal(1, (int)(UByte)new UInt(257));
        Assert.Equal(2, (int)(UByte)new ULong(258));
    }

    [Fact]
    public void FloatAndDecimalConversions()
    {
        Assert.Equal((int)(byte)200.9, (int)(UByte)200.9);
        Assert.Equal((int)(byte)255.0, (int)(UByte)255.0);
        Assert.Equal(0, (int)(UByte)double.NaN);
        Assert.Equal(0, (int)(UByte)(-1.0));
        Assert.Equal(255, (int)(UByte)double.PositiveInfinity);
        Assert.Equal(200, (int)(UByte)200.9f);
        Assert.Equal((double)(byte)200, new UByte(200).ToDouble());
        Assert.Equal((float)(byte)200, new UByte(200).ToSingle());
        Assert.Equal((decimal)(byte)200, new UByte(200).ToDecimal());
        Assert.Equal((int)(byte)5m, (int)(UByte)5m);
        Assert.Throws<System.OverflowException>(() => (UByte)(-1m));
        Assert.Throws<System.OverflowException>(() => (UByte)256m);
        Assert.Throws<System.OverflowException>(() => (UByte)1.5m);
    }
}
