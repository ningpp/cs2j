namespace dotnet.System
{
    internal static class UnsignedBits
    {
        // ================================================================
        // 8-bit domain (sbyte storage)
        // ================================================================
        public static sbyte Add8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) + ((int)b & 0xFF)); } }
        public static sbyte Sub8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) - ((int)b & 0xFF)); } }
        public static sbyte Mul8(sbyte a, sbyte b) { unchecked { return (sbyte)(((int)a & 0xFF) * ((int)b & 0xFF)); } }
        public static sbyte Div8(sbyte a, sbyte b) { return (sbyte)(((int)a & 0xFF) / ((int)b & 0xFF)); }
        public static sbyte Rem8(sbyte a, sbyte b) { return (sbyte)(((int)a & 0xFF) % ((int)b & 0xFF)); }
        public static sbyte And8(sbyte a, sbyte b) { return (sbyte)((int)a & (int)b); }
        public static sbyte Or8(sbyte a, sbyte b) { return (sbyte)((int)a | (int)b); }
        public static sbyte Xor8(sbyte a, sbyte b) { return (sbyte)((int)a ^ (int)b); }
        public static sbyte Not8(sbyte a) { return (sbyte)~(int)a; }
        public static sbyte Shl8(sbyte a, int n) { unchecked { return (sbyte)(((int)a & 0xFF) << (n & 31)); } }
        public static sbyte Shr8(sbyte a, int n) { return (sbyte)((((int)a & 0xFF) >>> (n & 31)) & 0xFF); }
        public static int Cmp8(sbyte a, sbyte b) { return ((int)a & 0xFF) - ((int)b & 0xFF); }

        // ================================================================
        // 16-bit domain (short storage)
        // ================================================================
        public static short Add16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) + ((int)b & 0xFFFF)); } }
        public static short Sub16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) - ((int)b & 0xFFFF)); } }
        public static short Mul16(short a, short b) { unchecked { return (short)(((int)a & 0xFFFF) * ((int)b & 0xFFFF)); } }
        public static short Div16(short a, short b) { return (short)(((int)a & 0xFFFF) / ((int)b & 0xFFFF)); }
        public static short Rem16(short a, short b) { return (short)(((int)a & 0xFFFF) % ((int)b & 0xFFFF)); }
        public static short And16(short a, short b) { return (short)((int)a & (int)b); }
        public static short Or16(short a, short b) { return (short)((int)a | (int)b); }
        public static short Xor16(short a, short b) { return (short)((int)a ^ (int)b); }
        public static short Not16(short a) { return (short)~(int)a; }
        public static short Shl16(short a, int n) { unchecked { return (short)(((int)a & 0xFFFF) << (n & 31)); } }
        public static short Shr16(short a, int n) { return (short)((((int)a & 0xFFFF) >>> (n & 31)) & 0xFFFF); }
        public static int Cmp16(short a, short b) { return ((int)a & 0xFFFF) - ((int)b & 0xFFFF); }

        // ================================================================
        // 32-bit domain (int storage, unsigned interpretation)
        // ================================================================
        public static int Add32(int a, int b) { unchecked { return a + b; } }
        public static int Sub32(int a, int b) { unchecked { return a - b; } }
        public static int Mul32(int a, int b) { unchecked { return a * b; } }
        public static int Divide32(int a, int b) { return (int)(((long)a & 0xFFFFFFFFL) / ((long)b & 0xFFFFFFFFL)); }
        public static int Remainder32(int a, int b) { return (int)(((long)a & 0xFFFFFFFFL) % ((long)b & 0xFFFFFFFFL)); }
        public static int And32(int a, int b) { return a & b; }
        public static int Or32(int a, int b) { return a | b; }
        public static int Xor32(int a, int b) { return a ^ b; }
        public static int Not32(int a) { return ~a; }
        public static int Shl32(int a, int n) { return a << (n & 31); }
        public static int Shr32(int a, int n) { return a >>> (n & 31); }
        public static int Cmp32(int a, int b)
        {
            long x = (long)a & 0xFFFFFFFFL;
            long y = (long)b & 0xFFFFFFFFL;
            return x < y ? -1 : x > y ? 1 : 0;
        }

        // ================================================================
        // 64-bit domain (long storage, unsigned interpretation)
        // ================================================================
        public static long Add64(long a, long b) { unchecked { return a + b; } }
        public static long Sub64(long a, long b) { unchecked { return a - b; } }
        public static long Mul64(long a, long b) { unchecked { return a * b; } }
        public static long And64(long a, long b) { return a & b; }
        public static long Or64(long a, long b) { return a | b; }
        public static long Xor64(long a, long b) { return a ^ b; }
        public static long Not64(long a) { return ~a; }
        public static long Shl64(long a, int n) { return a << (n & 63); }
        public static long Shr64(long a, int n) { return a >>> (n & 63); }
        public static int Cmp64(long a, long b)
        {
            if (a == b) return 0;
            if (a >= 0 && b < 0) return -1;
            if (a < 0 && b >= 0) return 1;
            return a < b ? -1 : 1;
        }
        public static long Divide64(long a, long b)
        {
            if (b == 0)
            {
                return a / b; // 自然抛 DivideByZeroException
            }
            if (b < 0)
            {
                return Cmp64(a, b) < 0 ? 0L : 1L;
            }
            if (a >= 0)
            {
                return a / b;
            }
            long quotient = unchecked(((a >>> 1) / b) * 2L);
            long remainder = unchecked(a - quotient * b);
            return quotient + (Cmp64(remainder, b) >= 0 ? 1L : 0L);
        }
        public static long Remainder64(long a, long b) { unchecked { return a - Divide64(a, b) * b; } }

        // ================================================================
        // Cross-width narrowing / extension
        // ================================================================
        public static short Extend8To16(sbyte v) { return (short)((int)v & 0xFF); }
        public static int Extend8To32(sbyte v) { return (int)v & 0xFF; }
        public static long Extend8To64(sbyte v) { return (long)((int)v & 0xFF); }
        public static int Extend16To32(short v) { return (int)v & 0xFFFF; }
        public static long Extend16To64(short v) { return (long)((int)v & 0xFFFF); }
        public static long Extend32To64(int v) { return (long)v & 0xFFFFFFFFL; }
        public static sbyte Narrow16To8(short v) { return (sbyte)v; }
        public static sbyte Narrow32To8(int v) { return (sbyte)(v & 0xFF); }
        public static short Narrow32To16(int v) { return (short)(v & 0xFFFF); }
        public static sbyte Narrow64To8(long v) { return (sbyte)(v & 0xFFL); }
        public static short Narrow64To16(long v) { return (short)(v & 0xFFFFL); }
        public static int Narrow64To32(long v) { return (int)v; }

        // ================================================================
        // Decimal / float canonical conversions
        // ================================================================
        public static decimal ToDecimal8(sbyte v) { return (decimal)((int)v & 0xFF); }
        public static decimal ToDecimal16(short v) { return (decimal)((int)v & 0xFFFF); }
        public static decimal ToDecimal32(int v) { return (decimal)((long)v & 0xFFFFFFFFL); }
        public static decimal ToDecimal64(long v)
        {
            unchecked { return (decimal)(v >>> 1) * 2m + (decimal)(v & 1L); }
        }
        public static sbyte FromDecimal8(decimal d)
        {
            if (d < 0m || d > 255m || d != global::System.Math.Truncate(d)) throw new global::System.OverflowException();
            return (sbyte)(int)d;
        }
        public static short FromDecimal16(decimal d)
        {
            if (d < 0m || d > 65535m || d != global::System.Math.Truncate(d)) throw new global::System.OverflowException();
            return (short)(int)d;
        }
        public static int FromDecimal32(decimal d)
        {
            if (d < 0m || d > 4294967295m || d != global::System.Math.Truncate(d)) throw new global::System.OverflowException();
            return (int)(long)d;
        }
        public static long FromDecimal64(decimal d)
        {
            if (d < 0m || d > 18446744073709551615m || d != global::System.Math.Truncate(d)) throw new global::System.OverflowException();
            unchecked { return d >= 9223372036854775808m ? (long)(d - 18446744073709551616m) : (long)d; }
        }
        public static double ToDouble8(sbyte v) { return (double)((int)v & 0xFF); }
        public static double ToDouble16(short v) { return (double)((int)v & 0xFFFF); }
        public static double ToDouble32(int v) { return (double)((long)v & 0xFFFFFFFFL); }
        public static double ToDouble64(long v) { return (double)ToDecimal64(v); }
        public static float ToSingle8(sbyte v) { return (float)((int)v & 0xFF); }
        public static float ToSingle16(short v) { return (float)((int)v & 0xFFFF); }
        public static float ToSingle32(int v) { return (float)((long)v & 0xFFFFFFFFL); }
        public static float ToSingle64(long v) { return (float)ToDecimal64(v); }
        // 可移植饱和规则（见规格 §5.3）：NaN/负→0；截断后超 MaxValue→MaxValue。
        public static sbyte FromDouble8(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return (sbyte)0;
            double t = global::System.Math.Truncate(d);
            if (t > 255.0) return unchecked((sbyte)-1);
            return (sbyte)(long)t;
        }
        public static short FromDouble16(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return (short)0;
            double t = global::System.Math.Truncate(d);
            if (t > 65535.0) return unchecked((short)-1);
            return (short)(long)t;
        }
        public static int FromDouble32(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return 0;
            double t = global::System.Math.Truncate(d);
            if (t > 4294967295.0) return -1;
            return (int)(long)t;
        }
        public static long FromDouble64(double d)
        {
            if (double.IsNaN(d) || d < 0.0) return 0L;
            double t = global::System.Math.Truncate(d);
            if (t >= 18446744073709551616.0) return -1L;
            unchecked
            {
                if (t >= 9223372036854775808.0) return (long)(t - 18446744073709551616.0);
                return (long)t;
            }
        }
        public static sbyte FromSingle8(float f) { return FromDouble8((double)f); }
        public static short FromSingle16(float f) { return FromDouble16((double)f); }
        public static int FromSingle32(float f) { return FromDouble32((double)f); }
        public static long FromSingle64(float f) { return FromDouble64((double)f); }

        // ================================================================
        // ToString helpers
        // ================================================================
        private static global::System.Globalization.CultureInfo Invariant
        {
            get { return global::System.Globalization.CultureInfo.InvariantCulture; }
        }
        public static string Plain8(sbyte v) { return ((int)v & 0xFF).ToString(Invariant); }
        public static string Plain16(short v) { return ((int)v & 0xFFFF).ToString(Invariant); }
        public static string Plain32(int v) { return ((long)v & 0xFFFFFFFFL).ToString(Invariant); }
        public static string Plain64(long v) { return ToDecimal64(v).ToString(Invariant); }
        // bits 传无符号典范位模式（8/16/32 位先零扩展为 long；64 位直接传）。
        public static string FormatHex(long bits, string format)
        {
            bool upper = format[0] == 'X';
            int precision = 0;
            for (int i = 1; i < format.Length; i++)
            {
                char c = format[i];
                if (c < '0' || c > '9') throw new global::System.FormatException("Invalid format string.");
                precision = precision * 10 + (c - '0');
            }
            int used;
            if (bits == 0)
            {
                used = 1;
            }
            else
            {
                used = 0;
                long tmp = bits;
                while (tmp != 0)
                {
                    used++;
                    tmp = tmp >>> 4;
                }
            }
            int len = used > precision ? used : precision;
            char[] chars = new char[len];
            string map = upper ? "0123456789ABCDEF" : "0123456789abcdef";
            for (int i = 0; i < len; i++)
            {
                int shift = 4 * (len - 1 - i);
                int nibble = shift >= 64 ? 0 : (int)(bits >>> shift) & 0xF;
                chars[i] = map[nibble];
            }
            return new string(chars);
        }

        // ================================================================
        // Parse / TryParse core（net10 探针行为，见设计 §6.6）
        // ================================================================
        private const global::System.Globalization.NumberStyles HexAllowed =
            global::System.Globalization.NumberStyles.AllowLeadingWhite
            | global::System.Globalization.NumberStyles.AllowTrailingWhite
            | global::System.Globalization.NumberStyles.AllowHexSpecifier;
        private const global::System.Globalization.NumberStyles BinAllowed =
            global::System.Globalization.NumberStyles.AllowLeadingWhite
            | global::System.Globalization.NumberStyles.AllowTrailingWhite
            | global::System.Globalization.NumberStyles.AllowBinarySpecifier;
        private const global::System.Globalization.NumberStyles DecAllowed =
            global::System.Globalization.NumberStyles.AllowLeadingWhite
            | global::System.Globalization.NumberStyles.AllowTrailingWhite
            | global::System.Globalization.NumberStyles.AllowLeadingSign
            | global::System.Globalization.NumberStyles.AllowTrailingSign
            | global::System.Globalization.NumberStyles.AllowParentheses
            | global::System.Globalization.NumberStyles.AllowDecimalPoint
            | global::System.Globalization.NumberStyles.AllowThousands
            | global::System.Globalization.NumberStyles.AllowExponent
            | global::System.Globalization.NumberStyles.AllowCurrencySymbol;

        public static bool TryParseUnsigned(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, decimal maxValue, out decimal result)
        {
            try
            {
                result = ParseUnsigned(s, style, provider, maxValue);
                return true;
            }
            catch (global::System.FormatException)
            {
                result = 0m;
                return false;
            }
            catch (global::System.OverflowException)
            {
                result = 0m;
                return false;
            }
        }

        public static decimal ParseUnsigned(string s, global::System.Globalization.NumberStyles style,
            global::System.IFormatProvider provider, decimal maxValue)
        {
            if (s == null) throw new global::System.ArgumentNullException("s");
            global::System.Globalization.NumberFormatInfo nfi = global::System.Globalization.NumberFormatInfo.GetInstance(provider);
            int mode = ValidateStyle(style);
            string t = TrimEnds(s, style);
            if (t.Length == 0) throw new global::System.FormatException("Input string was not in a correct format.");
            if (mode != 0)
            {
                return ParseRadix(t, mode == 1 ? 16 : 2, maxValue);
            }

            if ((style & global::System.Globalization.NumberStyles.AllowCurrencySymbol) != 0
                && nfi.CurrencySymbol.Length > 0
                && t.StartsWith(nfi.CurrencySymbol, global::System.StringComparison.Ordinal))
            {
                t = t.Substring(nfi.CurrencySymbol.Length);
            }
            bool negative = false;
            if ((style & global::System.Globalization.NumberStyles.AllowParentheses) != 0
                && t.Length >= 2 && t[0] == '(' && t[t.Length - 1] == ')')
            {
                t = t.Substring(1, t.Length - 2);
                negative = true;
            }
            bool leadSign = false;
            bool trailSign = false;
            if ((style & global::System.Globalization.NumberStyles.AllowLeadingSign) != 0
                && t.Length > 0 && IsSignChar(t[0], nfi))
            {
                if (t[0] == NegativeSignChar(nfi)) negative = true;
                t = t.Substring(1);
                leadSign = true;
            }
            if ((style & global::System.Globalization.NumberStyles.AllowTrailingSign) != 0
                && t.Length > 0 && IsSignChar(t[t.Length - 1], nfi))
            {
                if (t[t.Length - 1] == NegativeSignChar(nfi)) negative = true;
                t = t.Substring(0, t.Length - 1);
                trailSign = true;
            }
            if (leadSign && trailSign) throw new global::System.FormatException("Input string was not in a correct format.");
            if (t.Length == 0) throw new global::System.FormatException("Input string was not in a correct format.");

            int exponent = 0;
            int expPos = IndexOfExponentMarker(t);
            if (expPos >= 0)
            {
                if ((style & global::System.Globalization.NumberStyles.AllowExponent) == 0)
                    throw new global::System.FormatException("Input string was not in a correct format.");
                string expText = t.Substring(expPos + 1);
                t = t.Substring(0, expPos);
                exponent = ParseExponentDigits(expText);
            }

            string groupSep = nfi.NumberGroupSeparator;
            string decimalSep = nfi.NumberDecimalSeparator;
            int dot = t.IndexOf(decimalSep, global::System.StringComparison.Ordinal);
            if (dot >= 0)
            {
                if ((style & global::System.Globalization.NumberStyles.AllowDecimalPoint) == 0)
                    throw new global::System.FormatException("Input string was not in a correct format.");
                if (t.IndexOf(decimalSep, dot + decimalSep.Length, global::System.StringComparison.Ordinal) >= 0)
                    throw new global::System.FormatException("Input string was not in a correct format.");
            }
            string intPart = t;
            string fracPart = string.Empty;
            if (dot >= 0)
            {
                intPart = t.Substring(0, dot);
                fracPart = t.Substring(dot + decimalSep.Length);
            }
            if (groupSep.Length > 0 && intPart.IndexOf(groupSep, global::System.StringComparison.Ordinal) >= 0)
            {
                if ((style & global::System.Globalization.NumberStyles.AllowThousands) == 0)
                    throw new global::System.FormatException("Input string was not in a correct format.");
                if (intPart.StartsWith(groupSep, global::System.StringComparison.Ordinal)
                    || intPart.EndsWith(groupSep, global::System.StringComparison.Ordinal))
                    throw new global::System.FormatException("Input string was not in a correct format.");
                intPart = intPart.Replace(groupSep, string.Empty);
            }
            if (fracPart.IndexOf(groupSep, global::System.StringComparison.Ordinal) >= 0)
                throw new global::System.FormatException("Input string was not in a correct format.");

            // 先累加完整尾数（含小数位，10 进幂精确），再施加指数；
            // 最终统一做整数/范围校验。这样 "1.5e2" = 150 与原生一致，
            // 而 "1.5"（无指数）仍由末尾 Truncate 校验抛 Overflow。
            bool anyDigit = false;
            decimal acc = 0m;
            foreach (char c in intPart)
            {
                int digit = NativeDigit(c, nfi);
                if (digit < 0) throw new global::System.FormatException("Input string was not in a correct format.");
                acc = acc * 10m + digit;
                anyDigit = true;
            }
            if (!anyDigit) throw new global::System.FormatException("Input string was not in a correct format.");
            decimal fracScale = 1m;
            foreach (char c in fracPart)
            {
                int digit = NativeDigit(c, nfi);
                if (digit < 0) throw new global::System.FormatException("Input string was not in a correct format.");
                fracScale *= 10m;
                acc += digit / fracScale;
            }

            if (exponent != 0) acc = ApplyExponent(acc, exponent);
            if (negative) acc = -acc;
            if (acc != global::System.Math.Truncate(acc)) throw new global::System.OverflowException("Value was either too large or too small.");
            if (acc < 0m || acc > maxValue) throw new global::System.OverflowException("Value was either too large or too small.");
            return acc;
        }

        private static int ValidateStyle(global::System.Globalization.NumberStyles style)
        {
            if ((style & global::System.Globalization.NumberStyles.AllowHexSpecifier) != 0)
            {
                if ((style & ~HexAllowed) != 0)
                    throw new global::System.ArgumentException("The number style is not valid for this operation.");
                return 1;
            }
            if ((style & global::System.Globalization.NumberStyles.AllowBinarySpecifier) != 0)
            {
                if ((style & ~BinAllowed) != 0)
                    throw new global::System.ArgumentException("The number style is not valid for this operation.");
                return 2;
            }
            if ((style & ~DecAllowed) != 0)
                throw new global::System.ArgumentException("The number style is not valid for this operation.");
            return 0;
        }

        private static string TrimEnds(string s, global::System.Globalization.NumberStyles style)
        {
            int start = 0;
            int end = s.Length;
            if ((style & global::System.Globalization.NumberStyles.AllowLeadingWhite) != 0)
            {
                while (start < end && char.IsWhiteSpace(s[start])) start++;
            }
            if ((style & global::System.Globalization.NumberStyles.AllowTrailingWhite) != 0)
            {
                while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
            }
            return s.Substring(start, end - start);
        }

        private static decimal ParseRadix(string t, int radix, decimal maxValue)
        {
            decimal acc = 0m;
            bool any = false;
            foreach (char c in t)
            {
                int digit;
                if (radix == 16)
                {
                    if (c >= '0' && c <= '9') digit = c - '0';
                    else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                    else throw new global::System.FormatException("Input string was not in a correct format.");
                }
                else
                {
                    if (c == '0' || c == '1') digit = c - '0';
                    else throw new global::System.FormatException("Input string was not in a correct format.");
                }
                acc = acc * radix + digit;
                any = true;
            }
            if (!any) throw new global::System.FormatException("Input string was not in a correct format.");
            if (acc > maxValue) throw new global::System.OverflowException("Value was either too large or too small.");
            return acc;
        }

        private static bool IsSignChar(char c, global::System.Globalization.NumberFormatInfo nfi)
        {
            return (nfi.PositiveSign.Length == 1 && c == nfi.PositiveSign[0])
                || (nfi.NegativeSign.Length == 1 && c == nfi.NegativeSign[0]);
        }
        private static char NegativeSignChar(global::System.Globalization.NumberFormatInfo nfi)
        {
            return nfi.NegativeSign.Length == 1 ? nfi.NegativeSign[0] : '-';
        }
        private static int IndexOfExponentMarker(string t)
        {
            int pos = -1;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i] == 'e' || t[i] == 'E')
                {
                    if (pos >= 0) return -1;
                    pos = i;
                }
            }
            return pos;
        }
        private static int ParseExponentDigits(string text)
        {
            if (text.Length == 0 || text.Length > 10)
                throw new global::System.FormatException("Input string was not in a correct format.");
            int i = 0;
            bool neg = false;
            if (text[0] == '+' || text[0] == '-')
            {
                neg = text[0] == '-';
                i = 1;
            }
            if (i >= text.Length)
                throw new global::System.FormatException("Input string was not in a correct format.");
            long value = 0;
            for (; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    throw new global::System.FormatException("Input string was not in a correct format.");
                value = value * 10 + (text[i] - '0');
                if (value > 1000000)
                    throw new global::System.FormatException("Input string was not in a correct format.");
            }
            return neg ? -(int)value : (int)value;
        }
        private static decimal ApplyExponent(decimal acc, int exponent)
        {
            if (exponent > 0)
            {
                for (int i = 0; i < exponent; i++)
                {
                    acc *= 10m;
                    if (acc > 18446744073709551615m)
                        throw new global::System.OverflowException("Value was either too large or too small.");
                }
            }
            else
            {
                for (int i = 0; i < -exponent; i++)
                {
                    acc /= 10m;
                }
            }
            return acc;
        }
        private static int NativeDigit(char c, global::System.Globalization.NumberFormatInfo nfi)
        {
            if (c >= '0' && c <= '9') return c - '0';
            string[] nativeDigits = nfi.NativeDigits;
            for (int i = 0; i < 10; i++)
            {
                if (nativeDigits[i].Length == 1 && nativeDigits[i][0] == c) return i;
            }
            return -1;
        }
    }
}
