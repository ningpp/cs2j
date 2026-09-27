namespace dotnet.System
{
    public readonly struct ULong
        : global::System.IEquatable<ULong>, global::System.IComparable<ULong>, global::System.IComparable, global::System.IFormattable
    {
        private readonly long _v;

        public static readonly ULong MinValue = new ULong(0L);
        public static readonly ULong MaxValue = new ULong(-1L);

        public ULong(long value) { _v = value; }

        internal static ULong FromBits(long bits) { return new ULong(bits); }
        internal long Bits { get { return _v; } }

        public static ULong operator +(ULong a, ULong b) { return FromBits(UnsignedBits.Add64(a._v, b._v)); }
        public static ULong operator -(ULong a, ULong b) { return FromBits(UnsignedBits.Sub64(a._v, b._v)); }
        public static ULong operator *(ULong a, ULong b) { return FromBits(UnsignedBits.Mul64(a._v, b._v)); }
        public static ULong operator /(ULong a, ULong b) { return FromBits(UnsignedBits.Divide64(a._v, b._v)); }
        public static ULong operator %(ULong a, ULong b) { return FromBits(UnsignedBits.Remainder64(a._v, b._v)); }
        public static ULong operator &(ULong a, ULong b) { return FromBits(UnsignedBits.And64(a._v, b._v)); }
        public static ULong operator |(ULong a, ULong b) { return FromBits(UnsignedBits.Or64(a._v, b._v)); }
        public static ULong operator ^(ULong a, ULong b) { return FromBits(UnsignedBits.Xor64(a._v, b._v)); }
        public static ULong operator ~(ULong a) { return FromBits(UnsignedBits.Not64(a._v)); }
        public static ULong operator <<(ULong a, int n) { return FromBits(UnsignedBits.Shl64(a._v, n)); }
        public static ULong operator >>(ULong a, int n) { return FromBits(UnsignedBits.Shr64(a._v, n)); }
        public static ULong operator >>>(ULong a, int n) { return FromBits(UnsignedBits.Shr64(a._v, n)); }

        public bool Equals(ULong other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is ULong other && Equals(other); }
        public override int GetHashCode() { return unchecked((int)_v ^ (int)(_v >>> 32)); }
        public int CompareTo(ULong other) { return UnsignedBits.Cmp64(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is ULong other) return CompareTo(other);
            throw new global::System.ArgumentException("Object must be of type dotnet.System.ULong.");
        }
        public static bool operator ==(ULong a, ULong b) { return a._v == b._v; }
        public static bool operator !=(ULong a, ULong b) { return a._v != b._v; }
        public static bool operator <(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) < 0; }
        public static bool operator <=(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) <= 0; }
        public static bool operator >(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) > 0; }
        public static bool operator >=(ULong a, ULong b) { return UnsignedBits.Cmp64(a._v, b._v) >= 0; }

        public static explicit operator ULong(sbyte v) { return new ULong((long)v); }
        public static explicit operator ULong(short v) { return new ULong((long)v); }
        public static explicit operator ULong(int v) { return new ULong((long)v); }
        public static explicit operator ULong(long v) { return new ULong(v); }
        public static explicit operator sbyte(ULong v) { return (sbyte)v._v; }
        public static explicit operator short(ULong v) { return (short)v._v; }
        public static explicit operator int(ULong v) { return (int)v._v; }
        public static explicit operator long(ULong v) { return v._v; }

        public static implicit operator ULong(UByte v) { return FromBits(UnsignedBits.Extend8To64(v.Bits)); }
        public static implicit operator ULong(UShort v) { return FromBits(UnsignedBits.Extend16To64(v.Bits)); }
        public static implicit operator ULong(UInt v) { return FromBits(UnsignedBits.Extend32To64(v.Bits)); }

        public static explicit operator ULong(double v) { return FromBits(UnsignedBits.FromDouble64(v)); }
        public static explicit operator ULong(float v) { return FromBits(UnsignedBits.FromSingle64(v)); }
        public static explicit operator ULong(decimal v) { return FromBits(UnsignedBits.FromDecimal64(v)); }
        public static explicit operator double(ULong v) { return v.ToDouble(); }
        public static explicit operator float(ULong v) { return v.ToSingle(); }
        public static explicit operator decimal(ULong v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble64(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle64(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal64(_v); }

        public static ULong Parse(string s)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, null);
        }
        public static ULong Parse(string s, global::System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static ULong Parse(string s, global::System.IFormatProvider provider)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, provider);
        }
        public static ULong Parse(string s, global::System.Globalization.NumberStyles style, global::System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 18446744073709551615m);
            return new ULong(UnsignedBits.FromDecimal64(d));
        }
        public static bool TryParse(string s, out ULong result)
        {
            return TryParse(s, global::System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, out ULong result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 18446744073709551615m, out decimal d);
            result = ok ? new ULong(UnsignedBits.FromDecimal64(d)) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain64(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, global::System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain64(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex(_v, format);
            }
            // decimal 不支持 D（整型专属），需自行补零以匹配原生无符号 64 位整数行为。
            if (spec == 'D' && TailDigits(format))
            {
                string digits = UnsignedBits.Plain64(_v);
                int precision = format.Length == 1 ? 0 : int.Parse(format.Substring(1));
                return precision > digits.Length
                    ? new string('0', precision - digits.Length) + digits
                    : digits;
            }
            return UnsignedBits.ToDecimal64(_v)
                .ToString(format, provider ?? global::System.Globalization.CultureInfo.InvariantCulture);
        }
        string global::System.IFormattable.ToString(string format, global::System.IFormatProvider formatProvider)
        {
            return ToString(format, formatProvider);
        }
        private static bool TailDigits(string format)
        {
            for (int i = 1; i < format.Length; i++)
            {
                if (format[i] < '0' || format[i] > '9') return false;
            }
            return true;
        }
    }
}
