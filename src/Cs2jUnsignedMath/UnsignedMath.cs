// Cs2jUnsigned.UnsignedMath：无符号数学辅助类（唯一真源）。
// 本文件既是本库项目的源码，又被 CLI 以 EmbeddedResource 嵌入分发：
// unsigned-type 预处理把它释放到中间树 Cs2jUnsignedMath/ 下，主转换器
// 跳过该项目的转换，调用点经 TypeMappings 映射到 compat 手写类。
// 单文件模式（unsigned-type -i/-o）会把本文件原样写到输出目录，用户
// 自行编译后再走主转换，因此内容须保持"可转换 C# 子集"：
// - 无符号右移一律写 C# 11 的 >>>（转换器映射为 Java >>>）；
// - 不得把无符号除法/取模写成两侧 ulong 强转——转换器会把 (ulong) 擦成 (long)；
// - ulong → 浮点/decimal 一律经 ((ulong)v).ToString() 再 Parse。
namespace Cs2jUnsigned
{
    public static class UnsignedMath
    {
        // ---- Int32 domain ----
        public static int AddInt32(int a, int b) => unchecked(a + b);
        public static int SubtractInt32(int a, int b) => unchecked(a - b);
        public static int MultiplyInt32(int a, int b) => unchecked(a * b);
        public static int DivideInt32(int a, int b) => a / b;
        public static int RemainderInt32(int a, int b) => a % b;
        public static int AndInt32(int a, int b) => a & b;
        public static int OrInt32(int a, int b) => a | b;
        public static int XorInt32(int a, int b) => a ^ b;
        public static int NotInt32(int a) => ~a;
        public static int LeftShiftInt32(int a, int n) => a << (n & 31);
        public static int RightShiftInt32(int a, int n) => a >> (n & 31);
        public static int CompareInt32(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

        // ---- Int64 domain ----
        public static long AddInt64(long a, long b) => unchecked(a + b);
        public static long SubtractInt64(long a, long b) => unchecked(a - b);
        public static long MultiplyInt64(long a, long b) => unchecked(a * b);
        public static long DivideInt64(long a, long b) => a / b;
        public static long RemainderInt64(long a, long b) => a % b;
        public static long AndInt64(long a, long b) => a & b;
        public static long OrInt64(long a, long b) => a | b;
        public static long XorInt64(long a, long b) => a ^ b;
        public static int CompareInt64(long a, long b) => a < b ? -1 : a > b ? 1 : 0;

        // ---- UInt32 domain ----
        public static int AddUInt32(int a, int b) => unchecked(a + b);
        public static int SubtractUInt32(int a, int b) => unchecked(a - b);
        public static int MultiplyUInt32(int a, int b) => unchecked(a * b);
        public static int DivideUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) / (b & 0xFFFFFFFFL)));
        public static int RemainderUInt32(int a, int b) => unchecked((int)((a & 0xFFFFFFFFL) % (b & 0xFFFFFFFFL)));
        public static int AndUInt32(int a, int b) => a & b;
        public static int OrUInt32(int a, int b) => a | b;
        public static int XorUInt32(int a, int b) => a ^ b;
        public static int NotUInt32(int a) => ~a;
        public static int LeftShiftUInt32(int a, int n) => a << (n & 31);
        public static int RightShiftUInt32(int a, int n) => a >>> (n & 31);
        public static int CompareUInt32(int a, int b)
        {
            var ua = a & 0xFFFFFFFFL;
            var ub = b & 0xFFFFFFFFL;
            return ua < ub ? -1 : ua > ub ? 1 : 0;
        }

        // ---- UInt64 domain ----
        public static long AddUInt64(long a, long b) => unchecked(a + b);
        public static long SubtractUInt64(long a, long b) => unchecked(a - b);
        public static long MultiplyUInt64(long a, long b) => unchecked(a * b);
        public static long DivideUInt64(long a, long b)
        {
            if (b == 0)
            {
                // 让除零自然抛出 DivideByZeroException / ArithmeticException。
                return a / b;
            }

            if (b < 0)
            {
                // 无符号下 b >= 2^63,商只可能是 0 或 1。
                return CompareUInt64(a, b) < 0 ? 0 : 1;
            }

            if (a >= 0)
            {
                return a / b;
            }

            // a 的符号位为 1:先对折再修正(JDK Long.divideUnsigned 同款算法)。
            long quotient = unchecked(((a >>> 1) / b) * 2);
            long remainder = unchecked(a - quotient * b);
            return quotient + (CompareUInt64(remainder, b) >= 0 ? 1 : 0);
        }
        public static long RemainderUInt64(long a, long b) => unchecked(a - DivideUInt64(a, b) * b);
        public static long AndUInt64(long a, long b) => a & b;
        public static long OrUInt64(long a, long b) => a | b;
        public static long XorUInt64(long a, long b) => a ^ b;
        public static long NotUInt64(long a) => ~a;
        public static long LeftShiftUInt64(long a, int n) => a << (n & 63);
        public static long RightShiftUInt64(long a, int n) => a >>> (n & 63);
        public static int CompareUInt64(long a, long b)
        {
            if (a == b) return 0;
            if (a >= 0 && b < 0) return -1;
            if (a < 0 && b >= 0) return 1;
            return a < b ? -1 : 1;
        }

        // ---- Checked arithmetic (numeric-boundary pass, C15) ----
        // Overflow throws System.OverflowException; the Java counterparts throw
        // java.lang.ArithmeticException (see compat UnsignedMath).
        public static int AddCheckedInt32(int a, int b) => checked(a + b);
        public static int SubtractCheckedInt32(int a, int b) => checked(a - b);
        public static int MultiplyCheckedInt32(int a, int b) => checked(a * b);
        public static int NegateCheckedInt32(int a) => checked(-a);

        public static long AddCheckedInt64(long a, long b) => checked(a + b);
        public static long SubtractCheckedInt64(long a, long b) => checked(a - b);
        public static long MultiplyCheckedInt64(long a, long b) => checked(a * b);
        public static long NegateCheckedInt64(long a) => checked(-a);

        public static uint AddCheckedUInt32(uint a, uint b) => checked(a + b);
        public static uint SubtractCheckedUInt32(uint a, uint b) => checked(a - b);
        public static uint MultiplyCheckedUInt32(uint a, uint b) => checked(a * b);

        public static ulong AddCheckedUInt64(ulong a, ulong b) => checked(a + b);
        public static ulong SubtractCheckedUInt64(ulong a, ulong b) => checked(a - b);
        public static ulong MultiplyCheckedUInt64(ulong a, ulong b) => checked(a * b);

        // ---- Checked narrowing conversions (numeric-boundary pass, C15) ----
        // from Int32 domain (byte/sbyte/short/ushort/char sources normalize here)
        public static System.SByte ToSByteCheckedInt32(int v) => checked((sbyte)v);
        public static System.Byte ToByteCheckedInt32(int v) => checked((byte)v);
        public static System.Int16 ToInt16CheckedInt32(int v) => checked((short)v);
        public static System.UInt16 ToUInt16CheckedInt32(int v) => checked((ushort)v);
        public static char ToCharCheckedInt32(int v) => checked((char)v);
        public static uint ToUInt32CheckedInt32(int v) => checked((uint)v);
        public static ulong ToUInt64CheckedInt32(int v) => checked((ulong)v);

        // from UInt32 domain
        public static System.SByte ToSByteCheckedUInt32(uint v) => checked((sbyte)v);
        public static System.Byte ToByteCheckedUInt32(uint v) => checked((byte)v);
        public static System.Int16 ToInt16CheckedUInt32(uint v) => checked((short)v);
        public static System.UInt16 ToUInt16CheckedUInt32(uint v) => checked((ushort)v);
        public static char ToCharCheckedUInt32(uint v) => checked((char)v);
        public static int ToInt32CheckedUInt32(uint v) => checked((int)v);

        // from Int64 domain
        public static System.SByte ToSByteCheckedInt64(long v) => checked((sbyte)v);
        public static System.Byte ToByteCheckedInt64(long v) => checked((byte)v);
        public static System.Int16 ToInt16CheckedInt64(long v) => checked((short)v);
        public static System.UInt16 ToUInt16CheckedInt64(long v) => checked((ushort)v);
        public static char ToCharCheckedInt64(long v) => checked((char)v);
        public static int ToInt32CheckedInt64(long v) => checked((int)v);
        public static uint ToUInt32CheckedInt64(long v) => checked((uint)v);
        public static ulong ToUInt64CheckedInt64(long v) => checked((ulong)v);

        // from UInt64 domain
        public static System.SByte ToSByteCheckedUInt64(ulong v) => checked((sbyte)v);
        public static System.Byte ToByteCheckedUInt64(ulong v) => checked((byte)v);
        public static System.Int16 ToInt16CheckedUInt64(ulong v) => checked((short)v);
        public static System.UInt16 ToUInt16CheckedUInt64(ulong v) => checked((ushort)v);
        public static char ToCharCheckedUInt64(ulong v) => checked((char)v);
        public static int ToInt32CheckedUInt64(ulong v) => checked((int)v);
        public static uint ToUInt32CheckedUInt64(ulong v) => checked((uint)v);
        public static long ToInt64CheckedUInt64(ulong v) => checked((long)v);

        // ---- Conversions ----
        public static System.Byte ToByteInt32(int v) => unchecked((byte)(v & 0xFF));
        public static System.Byte ToByteInt64(long v) => unchecked((byte)((int)v & 0xFF));
        public static System.UInt16 ToUInt16Int32(int v) => unchecked((ushort)(v & 0xFFFF));
        public static System.UInt16 ToUInt16Int64(long v) => unchecked((ushort)((int)v & 0xFFFF));
        public static long ToInt64UInt32(int v) => v & 0xFFFFFFFFL;
        public static System.UInt64 ToUInt64UInt32(int v) => unchecked((ulong)(v & 0xFFFFFFFFL));
        public static int ToInt32UInt64(long v) => unchecked((int)v);
        public static double ToDoubleUInt32(int v) => (double)(v & 0xFFFFFFFFL);
        public static double ToDoubleUInt64(long v) => double.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static float ToSingleUInt32(int v) => (float)(v & 0xFFFFFFFFL);
        public static float ToSingleUInt64(long v) => float.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));
        public static decimal ToDecimalUInt32(int v) => (decimal)(v & 0xFFFFFFFFL);
        public static decimal ToDecimalUInt64(long v) => decimal.Parse(((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture));

        // ---- ToString (unsigned) ----
        public static string ToStringUInt32(int v) => (v & 0xFFFFFFFFL).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt32(int v, string? format) => ToStringUInt32(v, format, null);
        public static string ToStringUInt32(int v, System.IFormatProvider? provider) => ToStringUInt32(v);
        public static string ToStringUInt32(int v, string? format, System.IFormatProvider? provider) => format == null || format.Length == 0 ? ToStringUInt32(v) : (v & 0xFFFFFFFFL).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt64(long v) => ((ulong)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
        public static string ToStringUInt64(long v, string? format) => ToStringUInt64(v, format, null);
        public static string ToStringUInt64(long v, System.IFormatProvider? provider) => ToStringUInt64(v);
        public static string ToStringUInt64(long v, string? format, System.IFormatProvider? provider) => format == null || format.Length == 0 ? ToStringUInt64(v) : ((ulong)v).ToString(format, provider ?? System.Globalization.CultureInfo.InvariantCulture);
    }
}
