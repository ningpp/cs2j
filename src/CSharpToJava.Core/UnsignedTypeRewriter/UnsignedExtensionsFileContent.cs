namespace CSharpToJava.Core.UnsignedTypeRewriter;

public static class UnsignedExtensionsFileContent
{
    public const string FileName = "_Cs2jUnsignedExtensions.cs";

    public static string Generate() => """
        namespace Cs2jUnsigned;

        public static class UnsignedExtensions
        {
            // System.Byte
            public static byte Add(this byte a, byte b) => unchecked((byte)(a + b));
            public static byte Subtract(this byte a, byte b) => unchecked((byte)(a - b));
            public static byte Multiply(this byte a, byte b) => unchecked((byte)(a * b));
            public static byte Divide(this byte a, byte b) => (byte)(a / b);
            public static byte Remainder(this byte a, byte b) => (byte)(a % b);
            public static byte And(this byte a, byte b) => (byte)(a & b);
            public static byte Or(this byte a, byte b) => (byte)(a | b);
            public static byte Xor(this byte a, byte b) => (byte)(a ^ b);
            public static byte Not(this byte a) => unchecked((byte)~a);
            public static byte LeftShift(this byte a, int n) => unchecked((byte)(a << n));
            public static byte RightShift(this byte a, int n) => unchecked((byte)(a >> n));
            public static int CompareTo(this byte a, byte b) => a.CompareTo(b);
            public static sbyte ToSByte(this byte a) => unchecked((sbyte)a);
            public static short ToInt16(this byte a) => a;
            public static int ToInt32(this byte a) => a;
            public static long ToInt64(this byte a) => a;

            // System.UInt16
            public static ushort Add(this ushort a, ushort b) => unchecked((ushort)(a + b));
            public static ushort Subtract(this ushort a, ushort b) => unchecked((ushort)(a - b));
            public static ushort Multiply(this ushort a, ushort b) => unchecked((ushort)(a * b));
            public static ushort Divide(this ushort a, ushort b) => (ushort)(a / b);
            public static ushort Remainder(this ushort a, ushort b) => (ushort)(a % b);
            public static ushort And(this ushort a, ushort b) => (ushort)(a & b);
            public static ushort Or(this ushort a, ushort b) => (ushort)(a | b);
            public static ushort Xor(this ushort a, ushort b) => (ushort)(a ^ b);
            public static ushort Not(this ushort a) => unchecked((ushort)~a);
            public static ushort LeftShift(this ushort a, int n) => unchecked((ushort)(a << n));
            public static ushort RightShift(this ushort a, int n) => unchecked((ushort)(a >> n));
            public static int CompareTo(this ushort a, ushort b) => a.CompareTo(b);
            public static byte ToByte(this ushort a) => unchecked((byte)a);
            public static sbyte ToSByte(this ushort a) => unchecked((sbyte)a);
            public static short ToInt16(this ushort a) => unchecked((short)a);
            public static int ToInt32(this ushort a) => a;
            public static long ToInt64(this ushort a) => a;

            // System.UInt32
            public static uint Add(this uint a, uint b) => unchecked(a + b);
            public static uint Subtract(this uint a, uint b) => unchecked(a - b);
            public static uint Multiply(this uint a, uint b) => unchecked(a * b);
            public static uint Divide(this uint a, uint b) => a / b;
            public static uint Remainder(this uint a, uint b) => a % b;
            public static uint And(this uint a, uint b) => a & b;
            public static uint Or(this uint a, uint b) => a | b;
            public static uint Xor(this uint a, uint b) => a ^ b;
            public static uint Not(this uint a) => ~a;
            public static uint LeftShift(this uint a, int n) => a << n;
            public static uint RightShift(this uint a, int n) => a >> n;
            public static int CompareTo(this uint a, uint b) => a.CompareTo(b);
            public static byte ToByte(this uint a) => unchecked((byte)a);
            public static sbyte ToSByte(this uint a) => unchecked((sbyte)a);
            public static short ToInt16(this uint a) => unchecked((short)a);
            public static int ToInt32(this uint a) => unchecked((int)a);
            public static long ToInt64Unsigned(this uint a) => a;

            // System.UInt64
            public static ulong Add(this ulong a, ulong b) => unchecked(a + b);
            public static ulong Subtract(this ulong a, ulong b) => unchecked(a - b);
            public static ulong Multiply(this ulong a, ulong b) => unchecked(a * b);
            public static ulong Divide(this ulong a, ulong b) => a / b;
            public static ulong Remainder(this ulong a, ulong b) => a % b;
            public static ulong And(this ulong a, ulong b) => a & b;
            public static ulong Or(this ulong a, ulong b) => a | b;
            public static ulong Xor(this ulong a, ulong b) => a ^ b;
            public static ulong Not(this ulong a) => ~a;
            public static ulong LeftShift(this ulong a, int n) => a << n;
            public static ulong RightShift(this ulong a, int n) => a >> n;
            public static int CompareTo(this ulong a, ulong b) => a.CompareTo(b);
            public static byte ToByte(this ulong a) => unchecked((byte)a);
            public static sbyte ToSByte(this ulong a) => unchecked((sbyte)a);
            public static short ToInt16(this ulong a) => unchecked((short)a);
            public static int ToInt32(this ulong a) => unchecked((int)a);
            public static long ToInt64(this ulong a) => unchecked((long)a);
        }
        """;
}
