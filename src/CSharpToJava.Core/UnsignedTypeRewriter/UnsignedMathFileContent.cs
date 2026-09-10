namespace CSharpToJava.Core.UnsignedTypeRewriter;

public static class UnsignedMathFileContent
{
    public const string FileName = "_Cs2jUnsignedMath.cs";

    public static string Generate() => """
        namespace Cs2jUnsigned
        {
            internal static class UnsignedMath
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
                public static int RightShiftUInt32(int a, int n) => unchecked((int)((a & 0xFFFFFFFFL) >> (n & 31)));
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
                public static long DivideUInt64(long a, long b) => unchecked((long)((ulong)a / (ulong)b));
                public static long RemainderUInt64(long a, long b) => unchecked((long)((ulong)a % (ulong)b));
                public static long AndUInt64(long a, long b) => a & b;
                public static long OrUInt64(long a, long b) => a | b;
                public static long XorUInt64(long a, long b) => a ^ b;
                public static long NotUInt64(long a) => ~a;
                public static long LeftShiftUInt64(long a, int n) => a << (n & 63);
                public static long RightShiftUInt64(long a, int n) => unchecked((long)((ulong)a >> (n & 63)));
                public static int CompareUInt64(long a, long b)
                {
                    if (a == b) return 0;
                    if (a >= 0 && b < 0) return -1;
                    if (a < 0 && b >= 0) return 1;
                    return a < b ? -1 : 1;
                }

                // ---- Conversions ----
                public static System.Byte ToByteInt32(int v) => unchecked((byte)(v & 0xFF));
                public static System.Byte ToByteInt64(long v) => unchecked((byte)((int)v & 0xFF));
                public static System.UInt16 ToUInt16Int32(int v) => unchecked((ushort)(v & 0xFFFF));
                public static System.UInt16 ToUInt16Int64(long v) => unchecked((ushort)((int)v & 0xFFFF));
                public static long ToInt64UInt32(int v) => v & 0xFFFFFFFFL;
                public static System.UInt64 ToUInt64UInt32(int v) => unchecked((ulong)(v & 0xFFFFFFFFL));
                public static int ToInt32UInt64(long v) => unchecked((int)v);
                public static double ToDoubleUInt32(int v) => (double)(v & 0xFFFFFFFFL);
                public static double ToDoubleUInt64(long v) => (double)(ulong)v;
                public static float ToSingleUInt32(int v) => (float)(v & 0xFFFFFFFFL);
                public static float ToSingleUInt64(long v) => (float)(ulong)v;
                public static decimal ToDecimalUInt32(int v) => (decimal)(v & 0xFFFFFFFFL);
                public static decimal ToDecimalUInt64(long v) => (decimal)(ulong)v;

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
        """;
}
