namespace dotnet.System
{
    public readonly struct UInt
        : global::System.IEquatable<UInt>, global::System.IComparable<UInt>, global::System.IComparable, global::System.IFormattable
    {
        private readonly int _v;

        public static readonly UInt MinValue = new UInt(0);
        public static readonly UInt MaxValue = new UInt(-1);

        public UInt(int value) { _v = value; }
        public UInt(long value) { unchecked { _v = (int)value; } }

        internal static UInt FromBits(int bits) { return new UInt(bits); }
        internal int Bits { get { return _v; } }

        public static UInt operator +(UInt a, UInt b) { return FromBits(UnsignedBits.Add32(a._v, b._v)); }
        public static UInt operator -(UInt a, UInt b) { return FromBits(UnsignedBits.Sub32(a._v, b._v)); }
        public static UInt operator *(UInt a, UInt b) { return FromBits(UnsignedBits.Mul32(a._v, b._v)); }
        public static UInt operator /(UInt a, UInt b) { return FromBits(UnsignedBits.Divide32(a._v, b._v)); }
        public static UInt operator %(UInt a, UInt b) { return FromBits(UnsignedBits.Remainder32(a._v, b._v)); }
        public static UInt operator &(UInt a, UInt b) { return FromBits(UnsignedBits.And32(a._v, b._v)); }
        public static UInt operator |(UInt a, UInt b) { return FromBits(UnsignedBits.Or32(a._v, b._v)); }
        public static UInt operator ^(UInt a, UInt b) { return FromBits(UnsignedBits.Xor32(a._v, b._v)); }
        public static UInt operator ~(UInt a) { return FromBits(UnsignedBits.Not32(a._v)); }
        public static UInt operator <<(UInt a, int n) { return FromBits(UnsignedBits.Shl32(a._v, n)); }
        public static UInt operator >>(UInt a, int n) { return FromBits(UnsignedBits.Shr32(a._v, n)); }
        public static UInt operator >>>(UInt a, int n) { return FromBits(UnsignedBits.Shr32(a._v, n)); }

        public bool Equals(UInt other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UInt other && Equals(other); }
        public override int GetHashCode() { return _v; }
        public int CompareTo(UInt other) { return UnsignedBits.Cmp32(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UInt other) return CompareTo(other);
            throw new global::System.ArgumentException("Object must be of type dotnet.System.UInt.");
        }
        public static bool operator ==(UInt a, UInt b) { return a._v == b._v; }
        public static bool operator !=(UInt a, UInt b) { return a._v != b._v; }
        public static bool operator <(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) < 0; }
        public static bool operator <=(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) <= 0; }
        public static bool operator >(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) > 0; }
        public static bool operator >=(UInt a, UInt b) { return UnsignedBits.Cmp32(a._v, b._v) >= 0; }

        public static explicit operator UInt(sbyte v) { return new UInt((int)v); }
        public static explicit operator UInt(short v) { return new UInt((int)v); }
        public static explicit operator UInt(int v) { return new UInt(v); }
        public static explicit operator UInt(long v) { return new UInt(v); }
        public static explicit operator sbyte(UInt v) { return (sbyte)v._v; }
        public static explicit operator short(UInt v) { return (short)v._v; }
        public static explicit operator int(UInt v) { return v._v; }
        public static explicit operator long(UInt v) { return (long)v._v & 0xFFFFFFFFL; }

        public static implicit operator UInt(UByte v) { return FromBits(UnsignedBits.Extend8To32(v.Bits)); }
        public static implicit operator UInt(UShort v) { return FromBits(UnsignedBits.Extend16To32(v.Bits)); }
        public static explicit operator UInt(ULong v) { return FromBits(UnsignedBits.Narrow64To32(v.Bits)); }

        public static explicit operator UInt(double v) { return FromBits(UnsignedBits.FromDouble32(v)); }
        public static explicit operator UInt(float v) { return FromBits(UnsignedBits.FromSingle32(v)); }
        public static explicit operator UInt(decimal v) { return FromBits(UnsignedBits.FromDecimal32(v)); }
        public static explicit operator double(UInt v) { return v.ToDouble(); }
        public static explicit operator float(UInt v) { return v.ToSingle(); }
        public static explicit operator decimal(UInt v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble32(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle32(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal32(_v); }

        public static UInt Parse(string s)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, null);
        }
        public static UInt Parse(string s, global::System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UInt Parse(string s, global::System.IFormatProvider provider)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, provider);
        }
        public static UInt Parse(string s, global::System.Globalization.NumberStyles style, global::System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 4294967295m);
            return new UInt((long)d);
        }
        public static bool TryParse(string s, out UInt result)
        {
            return TryParse(s, global::System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, out UInt result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 4294967295m, out decimal d);
            result = ok ? new UInt((long)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain32(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, global::System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain32(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)_v & 0xFFFFFFFFL, format);
            }
            return ((long)_v & 0xFFFFFFFFL).ToString(format, provider ?? global::System.Globalization.CultureInfo.InvariantCulture);
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
