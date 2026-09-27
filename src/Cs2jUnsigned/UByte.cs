namespace dotnet.System
{
    public readonly struct UByte
        : global::System.IEquatable<UByte>, global::System.IComparable<UByte>, global::System.IComparable, global::System.IFormattable
    {
        private readonly sbyte _v;

        public static readonly UByte MinValue = new UByte((sbyte)0);
        public static readonly UByte MaxValue = new UByte(unchecked((sbyte)-1));

        public UByte(sbyte value) { unchecked { _v = (sbyte)((int)value & 0xFF); } }
        public UByte(int value) { unchecked { _v = (sbyte)(value & 0xFF); } }
        public UByte(long value) { unchecked { _v = (sbyte)(value & 0xFFL); } }

        internal static UByte FromBits(sbyte bits) { return new UByte(bits); }
        internal sbyte Bits { get { return _v; } }

        // ---- arithmetic / bitwise ----
        public static UByte operator +(UByte a, UByte b) { return FromBits(UnsignedBits.Add8(a._v, b._v)); }
        public static UByte operator -(UByte a, UByte b) { return FromBits(UnsignedBits.Sub8(a._v, b._v)); }
        public static UByte operator *(UByte a, UByte b) { return FromBits(UnsignedBits.Mul8(a._v, b._v)); }
        public static UByte operator /(UByte a, UByte b) { return FromBits(UnsignedBits.Div8(a._v, b._v)); }
        public static UByte operator %(UByte a, UByte b) { return FromBits(UnsignedBits.Rem8(a._v, b._v)); }
        public static UByte operator &(UByte a, UByte b) { return FromBits(UnsignedBits.And8(a._v, b._v)); }
        public static UByte operator |(UByte a, UByte b) { return FromBits(UnsignedBits.Or8(a._v, b._v)); }
        public static UByte operator ^(UByte a, UByte b) { return FromBits(UnsignedBits.Xor8(a._v, b._v)); }
        public static UByte operator ~(UByte a) { return FromBits(UnsignedBits.Not8(a._v)); }
        public static UByte operator <<(UByte a, int n) { return FromBits(UnsignedBits.Shl8(a._v, n)); }
        public static UByte operator >>(UByte a, int n) { return FromBits(UnsignedBits.Shr8(a._v, n)); }
        public static UByte operator >>>(UByte a, int n) { return FromBits(UnsignedBits.Shr8(a._v, n)); }

        // ---- equality / comparison ----
        public bool Equals(UByte other) { return _v == other._v; }
        public override bool Equals(object obj) { return obj is UByte other && Equals(other); }
        public override int GetHashCode() { return (int)_v & 0xFF; }
        public int CompareTo(UByte other) { return UnsignedBits.Cmp8(_v, other._v); }
        public int CompareTo(object obj)
        {
            if (obj == null) return 1;
            if (obj is UByte other) return CompareTo(other);
            throw new global::System.ArgumentException("Object must be of type dotnet.System.UByte.");
        }
        public static bool operator ==(UByte a, UByte b) { return a._v == b._v; }
        public static bool operator !=(UByte a, UByte b) { return a._v != b._v; }
        public static bool operator <(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) < 0; }
        public static bool operator <=(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) <= 0; }
        public static bool operator >(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) > 0; }
        public static bool operator >=(UByte a, UByte b) { return UnsignedBits.Cmp8(a._v, b._v) >= 0; }

        // ---- conversions: signed primitives ----
        public static explicit operator UByte(sbyte v) { return new UByte(v); }
        public static explicit operator UByte(short v) { return new UByte(v); }
        public static explicit operator UByte(int v) { return new UByte(v); }
        public static explicit operator UByte(long v) { return new UByte(v); }
        public static explicit operator sbyte(UByte v) { return v._v; }
        public static explicit operator short(UByte v) { return (short)((int)v._v & 0xFF); }
        public static explicit operator int(UByte v) { return (int)v._v & 0xFF; }
        public static explicit operator long(UByte v) { return (long)((int)v._v & 0xFF); }

        // ---- conversions: cross unsigned structs (narrowing defined here) ----
        public static explicit operator UByte(UShort v) { return FromBits(UnsignedBits.Narrow16To8(v.Bits)); }
        public static explicit operator UByte(UInt v) { return FromBits(UnsignedBits.Narrow32To8(v.Bits)); }
        public static explicit operator UByte(ULong v) { return FromBits(UnsignedBits.Narrow64To8(v.Bits)); }

        // ---- conversions: floating / decimal ----
        public static explicit operator UByte(double v) { return FromBits(UnsignedBits.FromDouble8(v)); }
        public static explicit operator UByte(float v) { return FromBits(UnsignedBits.FromSingle8(v)); }
        public static explicit operator UByte(decimal v) { return FromBits(UnsignedBits.FromDecimal8(v)); }
        public static explicit operator double(UByte v) { return v.ToDouble(); }
        public static explicit operator float(UByte v) { return v.ToSingle(); }
        public static explicit operator decimal(UByte v) { return v.ToDecimal(); }
        public double ToDouble() { return UnsignedBits.ToDouble8(_v); }
        public float ToSingle() { return UnsignedBits.ToSingle8(_v); }
        public decimal ToDecimal() { return UnsignedBits.ToDecimal8(_v); }

        // ---- parse / format ----
        public static UByte Parse(string s)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, null);
        }
        public static UByte Parse(string s, global::System.Globalization.NumberStyles style)
        {
            return Parse(s, style, null);
        }
        public static UByte Parse(string s, global::System.IFormatProvider provider)
        {
            return Parse(s, global::System.Globalization.NumberStyles.Integer, provider);
        }
        public static UByte Parse(string s, global::System.Globalization.NumberStyles style, global::System.IFormatProvider provider)
        {
            decimal d = UnsignedBits.ParseUnsigned(s, style, provider, 255m);
            return new UByte((int)d);
        }
        public static bool TryParse(string s, out UByte result)
        {
            return TryParse(s, global::System.Globalization.NumberStyles.Integer, null, out result);
        }
        public static bool TryParse(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, out UByte result)
        {
            bool ok = UnsignedBits.TryParseUnsigned(s, style, provider, 255m, out decimal d);
            result = ok ? new UByte((int)d) : default;
            return ok;
        }
        public override string ToString() { return UnsignedBits.Plain8(_v); }
        public string ToString(string format) { return ToString(format, null); }
        public string ToString(string format, global::System.IFormatProvider provider)
        {
            if (format == null || format.Length == 0) return UnsignedBits.Plain8(_v);
            char spec = format[0];
            if ((spec == 'X' || spec == 'x') && TailDigits(format))
            {
                return UnsignedBits.FormatHex((long)((int)_v & 0xFF), format);
            }
            return ((int)_v & 0xFF).ToString(format, provider ?? global::System.Globalization.CultureInfo.InvariantCulture);
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
