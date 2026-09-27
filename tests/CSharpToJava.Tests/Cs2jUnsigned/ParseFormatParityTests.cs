using dotnet.System;
using St = System.Globalization.NumberStyles;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class ParseFormatParityTests
{
    private static readonly System.IFormatProvider Inv =
        System.Globalization.CultureInfo.InvariantCulture;

    // 只放四类型公共合法集；越界串由 OverflowAndMalformed_ThrowSameExceptionTypes 覆盖。
    [Theory]
    [InlineData("0")]
    [InlineData("123")]
    [InlineData("255")]
    [InlineData(" 42 ")]
    public void Parse_Default_MatchesNative(string s)
    {
        Assert.Equal((int)byte.Parse(s), (int)UByte.Parse(s));
        Assert.Equal((int)ushort.Parse(s), (int)UShort.Parse(s));
        Assert.Equal((long)uint.Parse(s), (long)UInt.Parse(s));
        Assert.Equal(ulong.Parse(s), (ulong)(long)ULong.Parse(s));
    }

    [Fact]
    public void TryParse_MatchesNative()
    {
        foreach (string s in new[] { "0", "200", "65535", "4294967295", "18446744073709551615", "x", "" })
        {
            bool nb = byte.TryParse(s, out byte eb);
            bool ab = UByte.TryParse(s, out UByte abv);
            Assert.Equal(nb, ab);
            if (nb) Assert.Equal((int)eb, (int)abv);
            bool nu = ulong.TryParse(s, out ulong eu);
            bool au = ULong.TryParse(s, out ULong auv);
            Assert.Equal(nu, au);
            if (nu) Assert.Equal(eu, (ulong)(long)auv);
        }
        UByte tmp;
        Assert.False(UByte.TryParse("999", out tmp));
        Assert.Equal(default, tmp);
    }

    [Fact]
    public void OverflowAndMalformed_ThrowSameExceptionTypes()
    {
        Assert.Throws<System.OverflowException>(() => UByte.Parse("256"));
        Assert.Throws<System.OverflowException>(() => UShort.Parse("65536"));
        Assert.Throws<System.OverflowException>(() => UInt.Parse("4294967296"));
        Assert.Throws<System.OverflowException>(() => ULong.Parse("18446744073709551616"));
        Assert.Throws<System.FormatException>(() => UInt.Parse(""));
        Assert.Throws<System.FormatException>(() => UInt.Parse("   "));
        Assert.Throws<System.FormatException>(() => UInt.Parse("12a"));
        Assert.Throws<System.FormatException>(() => UInt.Parse("1+0"));
        Assert.Throws<System.FormatException>(() => UInt.Parse("1+"));
        Assert.Throws<System.ArgumentNullException>(() => UInt.Parse(null));
        Assert.Throws<System.OverflowException>(() => byte.Parse("-1", St.Integer, Inv));
        Assert.Throws<System.OverflowException>(() => UByte.Parse("-1", St.Integer, Inv));
        Assert.Throws<System.OverflowException>(() => ULong.Parse("(1)", St.AllowParentheses | St.AllowLeadingSign, Inv));
        Assert.Equal(0, (int)UByte.Parse("(0)", St.AllowParentheses, Inv));
        Assert.Equal(0, (int)UByte.Parse("0-", St.AllowTrailingSign, Inv));
        Assert.Throws<System.FormatException>(() => UInt.Parse("+1-", St.AllowLeadingSign | St.AllowTrailingSign, Inv));
    }

    [Fact]
    public void Styles_HexBinaryExponentGroups_MatchNative()
    {
        Assert.Equal((int)byte.Parse("FF", St.HexNumber, Inv), (int)UByte.Parse("FF", St.HexNumber, Inv));
        Assert.False(UByte.TryParse("0xFF", St.HexNumber, Inv, out _));
        Assert.False(UByte.TryParse("1ff", St.HexNumber, Inv, out _));
        Assert.Equal(10, (int)UByte.Parse("1010", St.AllowBinarySpecifier, Inv));
        Assert.False(UByte.TryParse("0b1010", St.AllowBinarySpecifier, Inv, out _));
        Assert.False(UByte.TryParse("100000000", St.AllowBinarySpecifier, Inv, out _));
        Assert.Equal(100L, (long)UInt.Parse("1e2", St.Integer | St.AllowExponent, Inv));
        Assert.Equal(100L, (long)UInt.Parse("1,00", St.Integer | St.AllowThousands, Inv));
        Assert.Equal(1000L, (long)UInt.Parse("1,,000", St.Integer | St.AllowThousands, Inv));
        Assert.Throws<System.FormatException>(() => UInt.Parse(",100", St.Integer | St.AllowThousands, Inv));
        Assert.Equal(1L, (long)UInt.Parse("1.0", St.Integer | St.AllowDecimalPoint, Inv));
        Assert.Throws<System.OverflowException>(() => UInt.Parse("1.5", St.Integer | St.AllowDecimalPoint, Inv));
        Assert.Throws<System.ArgumentException>(() => UInt.Parse("1", St.HexNumber | St.AllowThousands, Inv));
        Assert.Throws<System.ArgumentException>(() => UByte.Parse("1", St.AllowBinarySpecifier | St.AllowLeadingSign, Inv));
        Assert.Throws<System.ArgumentException>(() => UByte.TryParse("1", St.HexNumber | St.AllowThousands, Inv, out _));
    }

    [Fact]
    public void ToString_Formats_MatchNative()
    {
        byte b = 200; ushort s = 40000; uint i = 3000000000; ulong l = ulong.MaxValue;
        UByte ub = new UByte(200); UShort us = new UShort(40000);
        UInt ui = new UInt(3000000000); ULong ul = new ULong(-1L);
        foreach (string fmt in new[] { null, "", "D", "D3", "X", "X2", "X4", "N0", "G" })
        {
            Assert.Equal(b.ToString(fmt, Inv), ub.ToString(fmt, Inv));
            Assert.Equal(s.ToString(fmt, Inv), us.ToString(fmt, Inv));
            Assert.Equal(i.ToString(fmt, Inv), ui.ToString(fmt, Inv));
            Assert.Equal(l.ToString(fmt, Inv), ul.ToString(fmt, Inv));
        }
        Assert.Equal(1UL.ToString("X20", Inv), new ULong(1L).ToString("X20", Inv));
        Assert.Equal("00000000000000000001", new ULong(1L).ToString("X20", Inv));
        Assert.Equal("ffffffffffffffff", ul.ToString("x"));
        Assert.Equal(18446744073709551615m.ToString("N0", Inv), ul.ToString("N0", Inv));
        Assert.Equal("18446744073709551615", ul.ToString());
    }
}
