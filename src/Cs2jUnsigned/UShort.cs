namespace dotnet.System
{
    public readonly struct UShort
        : global::System.IEquatable<UShort>, global::System.IComparable<UShort>, global::System.IComparable, global::System.IFormattable
    {
        private readonly short _v;

        public static readonly UShort MinValue = new UShort((short)0);
        public static readonly UShort MaxValue = new UShort(unchecked((short)-1));

        public UShort(short value) { unchecked { _v = (short)((int)value & 0xFFFF); } }
        public UShort(int value) { unchecked { _v = (short)(value & 0xFFFF); } }
        public UShort(long value) { unchecked { _v = (short)(value & 0xFFFFL); } }

        internal static UShort FromBits(short bits) { return new UShort(bits); }
        internal short Bits { get { return _v; } }

        public static UShort operator +(UShort a, UShort b) { return FromBits(UnsignedBits.Add16(a._v, b._v)); }
        public static UShort operator -(UShort a, UShort b) { return FromBits(UnsignedBits.Sub16(a._v, b._v)); }
        public static UShort operator *(UShort a, UShort b) { return FromBits(UnsignedBits.Mul16(a._v, b._v)); }
        public static UShort operator /(UShort a, UShort b) { return FromBits(UnsignedBits.Div16(a._v, b._v)); }
        public static UShort operator %(UShort a, UShort b) { return FromBits(UnsignedBits.Rem16(a._v, b._v)); }
        public static UShort operator &(UShort a, UShort b) { return FromBits(UnsignedBits.And16(a._v, b._v)); }
        public static UShort operator |(UShort a, UShort b) { return FromBits(UnsignedBits.Or16(a._v, b._v)); }
        public static UShort operator ^(UShort a, UShort b) { return FromBits(UnsignedBits.Xor16(a._v, b._v)); }
        public static UShort operator ~(UShort a) { return FromBits(UnsignedBits.Not16(a._v)); }
        public static UShort operator <<(UShort a, int n) { return FromBits(UnsignedBits.Shl16(a._v, n)); }
        public static UShort operator >>(UShort a, int n) { return FromBits(UnsignedBits.Shr16(a._v, n)); }
        public static UShort operator >>>(UShort a, int n) { return FromBits(UnsignedBits.Shr16(a._v, n)); }

        public bool Equals(UShort other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UShort other && Equals(other); }
        public override int GetHashCode() { return (int)_v & 0xFFFF; }
        public int CompareTo(UShort other) { return UnsignedBits.Cmp16(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UShort other) return CompareTo(other);
            throw new global::System.ArgumentException("Object must be of type dotnet.System.UShort.");
        }
        public static bool operator ==(UShort a, UShort b) { return a._v == b._v; }
        public static bool operator !=(UShort a, UShort b) { return a._v != b._v; }
        public static bool operator <(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) < 0; }
        public static bool operator <=(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) <= 0; }
        public static bool operator >(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) > 0; }
        public static bool operator >=(UShort a, UShort b) { return UnsignedBits.Cmp16(a._v, b._v) >= 0; }

        public static explicit operator UShort(sbyte v) { return new UShort((short)v); }
        public static explicit operator UShort(short v) { return new UShort(v); }
        public static explicit operator UShort(int v) { return new UShort(v); }
        public static explicit operator UShort(long v) { return new UShort(v); }
        public static explicit operator sbyte(UShort v) { return (sbyte)v._v; }
        public static explicit operator short(UShort v) { return v._v; }
        public static explicit operator int(UShort v) { return (int)v._v & 0xFFFF; }
        public static explicit operator long(UShort v) { return (long)((int)v._v & 0xFFFF); }

        public static implicit operator UShort(UByte v) { return FromBits(UnsignedBits.Extend8To16(v.Bits)); }
        public static explicit operator UShort(UInt v) { return FromBits(UnsignedBits.Narrow32To16(v.Bits)); }
        public static explicit operator UShort(ULong v) { return FromBits(UnsignedBits.Narrow64To16(v.Bits)); }

        public static explicit operator UShort(double v) { return FromBits(UnsignedBits.FromDouble16(v)); }
        public static explicit operator UShort(float v) { return FromBits(UnsignedBits.FromSingle16(v)); }
        public static explicit operator UShort(decimal v) { return FromBits(UnsignedBits.FromDecimal16(v)); }
        public static explicit operator double(UShort v) { return v.ToDouble(); }
        public static explicit operator float(UShort v) { return v.ToSingle(); }
        public static explicit operator decimal(UShort v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble16(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle16(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal16(_v); }

        public static UShort Parse(string s)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, null);
        }
        public static UShort Parse(string s, global::System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UShort Parse(string s, global::System.IFormatProvider provider)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, provider);
        }
        public static UShort Parse(string s, global::System.Globalization.NumberStyles style, global::System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 65535m);
            return new UShort((int)d);
        }
        public static bool TryParse(string s, out UShort result)
        {
            return TryParse(s, global::System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, out UShort result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 65535m, out decimal d);
            result = ok ? new UShort((int)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain16(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, global::System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain16(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)((int)_v & 0xFFFF), format);
            }
            return ((int)_v & 0xFFFF).ToString(format, provider ?? global::System.Globalization.CultureInfo.InvariantCulture);
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
