using dotnet.System;
using St = System.Globalization.NumberStyles;

namespace CSharpToJava.Tests.Cs2jUnsigned;

public class UnsignedBitsTests
{
    [Fact]
    public void Arithmetic8_16_WrapsAndMasks()
    {
        Assert.Equal(unchecked((sbyte)0), UnsignedBits.Add8(unchecked((sbyte)-1), (sbyte)1));      // 255+1
        Assert.Equal(unchecked((sbyte)200), UnsignedBits.Mul8((sbyte)20, (sbyte)10));
        Assert.Equal(unchecked((sbyte)0), UnsignedBits.Shl8((sbyte)1, 8));
        Assert.Equal(unchecked((sbyte)1), UnsignedBits.Shl8((sbyte)1, 32));
        Assert.Equal(unchecked((sbyte)0x0F), UnsignedBits.Shr8(unchecked((sbyte)0xF0), 4));
        Assert.Equal(unchecked((sbyte)0xFF), UnsignedBits.Not8((sbyte)0));
        Assert.Equal(100, UnsignedBits.Cmp8(unchecked((sbyte)200), (sbyte)100));
        Assert.Equal(unchecked((short)0), UnsignedBits.Add16(unchecked((short)-1), (short)1));     // 65535+1
        Assert.Equal(unchecked((short)0), UnsignedBits.Shl16((short)1, 16));
        Assert.Equal(65534, UnsignedBits.Cmp16(unchecked((short)-1), (short)1));
        Assert.Equal(unchecked((sbyte)0x34), UnsignedBits.Narrow32To8(0x1234));
    }

    [Fact]
    public void Arithmetic32_DivideUsesUnsignedOrder()
    {
        Assert.Equal(1, UnsignedBits.Divide32(-1, -2));        // 0xFFFFFFFF / 0xFFFFFFFE = 1
        Assert.Equal(2147483648L, (long)UnsignedBits.Divide32(unchecked((int)0x80000000), 1) & 0xFFFFFFFFL);
        Assert.Equal(1, UnsignedBits.Cmp32(-1, 1));
        Assert.Equal(-1, UnsignedBits.Cmp32(1, -1));
        Assert.Equal(1, UnsignedBits.Shr32(-1, 31));           // 0xFFFFFFFF >>> 31 = 1
    }

    [Fact]
    public void Arithmetic64_HighBitDivisorFold()
    {
        Assert.Equal(1L, UnsignedBits.Divide64(-1L, -1L));          // max/max
        Assert.Equal(0L, UnsignedBits.Divide64(-2L, -1L));          // (max-1)/max
        Assert.Equal(9223372036854775807L, UnsignedBits.Divide64(-1L, 2L));
        Assert.Equal(1L, UnsignedBits.Remainder64(-1L, 2L));
        Assert.Equal(3074457345618258602L, UnsignedBits.Divide64(unchecked((long)0x8000000000000000), 3L));
        Assert.Equal(1, UnsignedBits.Cmp64(-1L, 1L));
        Assert.Equal(-1, UnsignedBits.Cmp64(1L, -1L));
        Assert.Equal(0, UnsignedBits.Cmp64(-2L, -2L));
        Assert.Throws<System.DivideByZeroException>(() => UnsignedBits.Divide64(1L, 0L));
    }

    [Fact]
    public void DecimalFold64_RoundTrips()
    {
        Assert.Equal(18446744073709551615m, UnsignedBits.ToDecimal64(-1L));
        Assert.Equal(9223372036854775808m, UnsignedBits.ToDecimal64(unchecked((long)0x8000000000000000)));
        Assert.Equal(-1L, UnsignedBits.FromDecimal64(18446744073709551615m));
        Assert.Equal(unchecked((long)0x8000000000000000), UnsignedBits.FromDecimal64(9223372036854775808m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(-1m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(18446744073709551616m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.FromDecimal64(1.5m));
    }

    [Fact]
    public void DoubleConversions_MatchOracle()
    {
        Assert.Equal(0, UnsignedBits.FromDouble8(double.NaN));
        Assert.Equal(unchecked((sbyte)-1), UnsignedBits.FromDouble8(double.PositiveInfinity));
        Assert.Equal(unchecked((sbyte)200), UnsignedBits.FromDouble8(200.9));
        Assert.Equal(-1, UnsignedBits.FromDouble32(4294967295.0));
        Assert.Equal(0, UnsignedBits.FromDouble32(-0.5));
        Assert.Equal(-1L, UnsignedBits.FromDouble64(18446744073709551615.0));
        Assert.Equal(unchecked((long)0x8000000000000000), UnsignedBits.FromDouble64(9223372036854775808.0));
        Assert.Equal(0L, UnsignedBits.FromDouble64(double.NaN));
        Assert.Equal(18446744073709551615.0.ToString("R"), UnsignedBits.ToDouble64(-1L).ToString("R"));
    }

    [Fact]
    public void FormatHex_MinimalNibblesAndPadding()
    {
        Assert.Equal("1", UnsignedBits.FormatHex(1L, "X"));
        Assert.Equal("001", UnsignedBits.FormatHex(1L, "X3"));
        Assert.Equal("ffffffffffffffff", UnsignedBits.FormatHex(-1L, "x"));
        Assert.Equal("0000FFFFFFFFFFFFFFFF", UnsignedBits.FormatHex(-1L, "X20"));
        Assert.Equal("FF", UnsignedBits.FormatHex(255L, "X2"));
    }

    [Fact]
    public void ParseUnsigned_OracleBehaviors()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(100m, UnsignedBits.ParseUnsigned("1e2", St.Integer | St.AllowExponent, inv, 4294967295m));
        Assert.Equal(100m, UnsignedBits.ParseUnsigned("1,00", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Equal(1000m, UnsignedBits.ParseUnsigned("1,,000", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned(",100", St.Integer | St.AllowThousands, inv, 4294967295m));
        Assert.Equal(1m, UnsignedBits.ParseUnsigned("1.0", St.Integer | St.AllowDecimalPoint, inv, 4294967295m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.ParseUnsigned("1.5", St.Integer | St.AllowDecimalPoint, inv, 4294967295m));
        Assert.Throws<System.OverflowException>(() => UnsignedBits.ParseUnsigned("(1)", St.AllowParentheses | St.AllowLeadingSign, inv, 4294967295m));
        Assert.Equal(0m, UnsignedBits.ParseUnsigned("(0)", St.AllowParentheses, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("+1-", St.AllowLeadingSign | St.AllowTrailingSign, inv, 4294967295m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("0xFF", St.HexNumber, inv, 4294967295m));
        Assert.Equal(10m, UnsignedBits.ParseUnsigned("1010", St.AllowBinarySpecifier, inv, 255m));
        Assert.Throws<System.FormatException>(() => UnsignedBits.ParseUnsigned("0b1010", St.AllowBinarySpecifier, inv, 255m));
        Assert.Throws<System.ArgumentException>(() => UnsignedBits.ParseUnsigned("1", St.HexNumber | St.AllowThousands, inv, 4294967295m));
        Assert.Throws<System.ArgumentException>(() => UnsignedBits.ParseUnsigned("1", St.AllowBinarySpecifier | St.AllowLeadingSign, inv, 255m));
        Assert.False(UnsignedBits.TryParseUnsigned("999", St.Integer, inv, 255m, out _));
        Assert.False(UnsignedBits.TryParseUnsigned("0xFF", St.HexNumber, inv, 255m, out _));
        Assert.Throws<System.ArgumentNullException>(() => UnsignedBits.ParseUnsigned(null, St.Integer, inv, 255m));
    }
}
