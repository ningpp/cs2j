package io.github.ningpp.compat;

import java.math.BigInteger;

/**
 * C# 无符号数学辅助类 Cs2jUnsigned.UnsignedMath 的手写对应物（TypeMappings
 * 把调用点路由到本类）。方法全集与 C# 版同名同参数个数（camelCase 命名与
 * 转换器生成调用一致），直接委托 JDK unsigned API。仅服务改写调用点的静态
 * 数学运算，不承担 UInt/ULong/UIntPtr 的类型替换职责。
 */
public final class UnsignedMath {
    private UnsignedMath() {
    }

    // ---- Int32 domain ----
    public static int addInt32(int a, int b) { return a + b; }
    public static int subtractInt32(int a, int b) { return a - b; }
    public static int multiplyInt32(int a, int b) { return a * b; }
    public static int divideInt32(int a, int b) { return a / b; }
    public static int remainderInt32(int a, int b) { return a % b; }
    public static int andInt32(int a, int b) { return a & b; }
    public static int orInt32(int a, int b) { return a | b; }
    public static int xorInt32(int a, int b) { return a ^ b; }
    public static int notInt32(int a) { return ~a; }
    public static int leftShiftInt32(int a, int n) { return a << (n & 31); }
    public static int rightShiftInt32(int a, int n) { return a >> (n & 31); }
    public static int compareInt32(int a, int b) { return Integer.compare(a, b); }

    // ---- Int64 domain ----
    public static long addInt64(long a, long b) { return a + b; }
    public static long subtractInt64(long a, long b) { return a - b; }
    public static long multiplyInt64(long a, long b) { return a * b; }
    public static long divideInt64(long a, long b) { return a / b; }
    public static long remainderInt64(long a, long b) { return a % b; }
    public static long andInt64(long a, long b) { return a & b; }
    public static long orInt64(long a, long b) { return a | b; }
    public static long xorInt64(long a, long b) { return a ^ b; }
    public static int compareInt64(long a, long b) { return Long.compare(a, b); }

    // ---- UInt32 domain ----
    public static int addUInt32(int a, int b) { return a + b; }
    public static int subtractUInt32(int a, int b) { return a - b; }
    public static int multiplyUInt32(int a, int b) { return a * b; }
    public static int divideUInt32(int a, int b) { return Integer.divideUnsigned(a, b); }
    public static int remainderUInt32(int a, int b) { return Integer.remainderUnsigned(a, b); }
    public static int andUInt32(int a, int b) { return a & b; }
    public static int orUInt32(int a, int b) { return a | b; }
    public static int xorUInt32(int a, int b) { return a ^ b; }
    public static int notUInt32(int a) { return ~a; }
    public static int leftShiftUInt32(int a, int n) { return a << (n & 31); }
    public static int rightShiftUInt32(int a, int n) { return a >>> (n & 31); }
    public static int compareUInt32(int a, int b) { return Integer.compareUnsigned(a, b); }

    // ---- UInt64 domain ----
    public static long addUInt64(long a, long b) { return a + b; }
    public static long subtractUInt64(long a, long b) { return a - b; }
    public static long multiplyUInt64(long a, long b) { return a * b; }
    public static long divideUInt64(long a, long b) { return Long.divideUnsigned(a, b); }
    public static long remainderUInt64(long a, long b) { return Long.remainderUnsigned(a, b); }
    public static long andUInt64(long a, long b) { return a & b; }
    public static long orUInt64(long a, long b) { return a | b; }
    public static long xorUInt64(long a, long b) { return a ^ b; }
    public static long notUInt64(long a) { return ~a; }
    public static long leftShiftUInt64(long a, int n) { return a << (n & 63); }
    public static long rightShiftUInt64(long a, int n) { return a >>> (n & 63); }
    public static int compareUInt64(long a, long b) { return Long.compareUnsigned(a, b); }

    // ---- Checked arithmetic (numeric-boundary C15) ----
    // .NET throws System.OverflowException; Java reports java.lang.ArithmeticException.
    public static int addCheckedInt32(int a, int b) { return Math.addExact(a, b); }
    public static int subtractCheckedInt32(int a, int b) { return Math.subtractExact(a, b); }
    public static int multiplyCheckedInt32(int a, int b) { return Math.multiplyExact(a, b); }
    public static int negateCheckedInt32(int a) { return Math.negateExact(a); }

    public static long addCheckedInt64(long a, long b) { return Math.addExact(a, b); }
    public static long subtractCheckedInt64(long a, long b) { return Math.subtractExact(a, b); }
    public static long multiplyCheckedInt64(long a, long b) { return Math.multiplyExact(a, b); }
    public static long negateCheckedInt64(long a) { return Math.negateExact(a); }

    public static int addCheckedUInt32(int a, int b) {
        long sum = (a & 0xFFFFFFFFL) + (b & 0xFFFFFFFFL);
        if (sum > 0xFFFFFFFFL) {
            throw new ArithmeticException("overflow");
        }
        return (int) sum;
    }

    public static int subtractCheckedUInt32(int a, int b) {
        long diff = (a & 0xFFFFFFFFL) - (b & 0xFFFFFFFFL);
        if (diff < 0L) {
            throw new ArithmeticException("overflow");
        }
        return (int) diff;
    }

    public static int multiplyCheckedUInt32(int a, int b) {
        long product = (a & 0xFFFFFFFFL) * (b & 0xFFFFFFFFL);
        if (product > 0xFFFFFFFFL) {
            throw new ArithmeticException("overflow");
        }
        return (int) product;
    }

    public static long addCheckedUInt64(long a, long b) {
        long sum = a + b;
        if (Long.compareUnsigned(sum, a) < 0) {
            throw new ArithmeticException("overflow");
        }
        return sum;
    }

    public static long subtractCheckedUInt64(long a, long b) {
        long diff = a - b;
        if (Long.compareUnsigned(diff, a) > 0) {
            throw new ArithmeticException("overflow");
        }
        return diff;
    }

    public static long multiplyCheckedUInt64(long a, long b) {
        BigInteger product = toUnsignedBigInteger(a).multiply(toUnsignedBigInteger(b));
        if (product.bitLength() > 64) {
            throw new ArithmeticException("overflow");
        }
        return product.longValue();
    }

    private static BigInteger toUnsignedBigInteger(long v) {
        byte[] bytes = new byte[8];
        long x = v;
        for (int i = 7; i >= 0; i--) {
            bytes[i] = (byte) (x & 0xFFL);
            x >>>= 8;
        }
        return new BigInteger(1, bytes);
    }

    // ---- Checked narrowing conversions (numeric-boundary C15) ----
    // Narrow/int/uint results return int, matching the toByteInt32 convention;
    // long/ulong-domain results return long.
    private static int checkedRange(long v, long min, long max) {
        if (v < min || v > max) {
            throw new ArithmeticException("overflow");
        }
        return (int) v;
    }

    private static int checkedRangeUnsignedSource(long unsignedValue, long max) {
        if (unsignedValue < 0L || Long.compareUnsigned(unsignedValue, max) > 0) {
            throw new ArithmeticException("overflow");
        }
        return (int) unsignedValue;
    }

    // from Int32 domain
    public static int toSByteCheckedInt32(int v) { return checkedRange(v, -128L, 127L); }
    public static int toByteCheckedInt32(int v) { return checkedRange(v, 0L, 255L); }
    public static int toInt16CheckedInt32(int v) { return checkedRange(v, -32768L, 32767L); }
    public static int toUInt16CheckedInt32(int v) { return checkedRange(v, 0L, 65535L); }
    public static int toCharCheckedInt32(int v) { return checkedRange(v, 0L, 65535L); }
    public static int toUInt32CheckedInt32(int v) { return checkedRange(v, 0L, 0xFFFFFFFFL); }
    public static long toUInt64CheckedInt32(int v) {
        if (v < 0) {
            throw new ArithmeticException("overflow");
        }
        return v;
    }

    // from UInt32 domain
    public static int toSByteCheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 127L); }
    public static int toByteCheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 255L); }
    public static int toInt16CheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 32767L); }
    public static int toUInt16CheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 65535L); }
    public static int toCharCheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 65535L); }
    public static int toInt32CheckedUInt32(int v) { return checkedRange(v & 0xFFFFFFFFL, 0L, 0x7FFFFFFFL); }

    // from Int64 domain
    public static int toSByteCheckedInt64(long v) { return checkedRange(v, -128L, 127L); }
    public static int toByteCheckedInt64(long v) { return checkedRange(v, 0L, 255L); }
    public static int toInt16CheckedInt64(long v) { return checkedRange(v, -32768L, 32767L); }
    public static int toUInt16CheckedInt64(long v) { return checkedRange(v, 0L, 65535L); }
    public static int toCharCheckedInt64(long v) { return checkedRange(v, 0L, 65535L); }
    public static int toInt32CheckedInt64(long v) {
        return checkedRange(v, Integer.MIN_VALUE, Integer.MAX_VALUE);
    }
    public static int toUInt32CheckedInt64(long v) { return checkedRange(v, 0L, 0xFFFFFFFFL); }
    public static long toUInt64CheckedInt64(long v) {
        if (v < 0L) {
            throw new ArithmeticException("overflow");
        }
        return v;
    }

    // from UInt64 domain
    public static int toSByteCheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 127L); }
    public static int toByteCheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 255L); }
    public static int toInt16CheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 32767L); }
    public static int toUInt16CheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 65535L); }
    public static int toCharCheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 65535L); }
    public static int toInt32CheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 0x7FFFFFFFL); }
    public static int toUInt32CheckedUInt64(long v) { return checkedRangeUnsignedSource(v, 0xFFFFFFFFL); }
    public static long toInt64CheckedUInt64(long v) {
        if (v < 0L) {
            throw new ArithmeticException("overflow");
        }
        return v;
    }

    // ---- Conversions ----
    public static int toByteInt32(int v) { return v & 0xFF; }
    public static int toByteInt64(long v) { return (int) v & 0xFF; }
    public static int toUInt16Int32(int v) { return v & 0xFFFF; }
    public static int toUInt16Int64(long v) { return (int) v & 0xFFFF; }
    public static long toInt64UInt32(int v) { return v & 0xFFFFFFFFL; }
    public static long toUInt64UInt32(int v) { return v & 0xFFFFFFFFL; }
    public static int toInt32UInt64(long v) { return (int) v; }
    public static double toDoubleUInt32(int v) { return (double) (v & 0xFFFFFFFFL); }
    public static double toDoubleUInt64(long v) { return Double.parseDouble(Long.toUnsignedString(v)); }
    public static float toSingleUInt32(int v) { return (float) (v & 0xFFFFFFFFL); }
    public static float toSingleUInt64(long v) { return Float.parseFloat(Long.toUnsignedString(v)); }
    public static Decimal toDecimalUInt32(int v) { return Decimal.valueOf(v & 0xFFFFFFFFL); }
    public static Decimal toDecimalUInt64(long v) { return Decimal.parse(Long.toUnsignedString(v)); }

    // ---- ToString (unsigned) ----
    public static String toStringUInt32(int v) { return Integer.toUnsignedString(v); }
    public static String toStringUInt32(int v, String format) { return toStringUInt32(v, format, null); }
    public static String toStringUInt32(int v, IFormatProvider provider) { return toStringUInt32(v); }
    public static String toStringUInt32(int v, String format, IFormatProvider provider) {
        if (format == null || format.isEmpty()) {
            return toStringUInt32(v);
        }
        // v & 0xFFFFFFFFL 非负，Long 幅值即无符号值
        return MathHelper.formatNumeric(format, v & 0xFFFFFFFFL);
    }

    public static String toStringUInt64(long v) { return Long.toUnsignedString(v); }
    public static String toStringUInt64(long v, String format) { return toStringUInt64(v, format, null); }
    public static String toStringUInt64(long v, IFormatProvider provider) { return toStringUInt64(v); }
    public static String toStringUInt64(long v, String format, IFormatProvider provider) {
        if (format == null || format.isEmpty()) {
            return toStringUInt64(v);
        }
        // BigInteger 携带完整无符号幅值，X/x/D 格式按 C# ulong 语义输出
        return MathHelper.formatNumeric(format, new BigInteger(Long.toUnsignedString(v)));
    }
}
