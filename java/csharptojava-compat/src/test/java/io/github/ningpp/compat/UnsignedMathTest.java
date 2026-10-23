package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class UnsignedMathTest {

    private static final int[] INT_EDGE = {
        0, 1, -1, Integer.MIN_VALUE, Integer.MAX_VALUE, Integer.MAX_VALUE / 2, 0x12345678, -0x12345678};
    private static final long[] LONG_EDGE = {
        0L, 1L, -1L, Long.MIN_VALUE, Long.MAX_VALUE, Long.MAX_VALUE / 2L, 0x123456789ABCDEFL, -0x123456789ABCDEFL};

    // ---- 改写器调用闭集：算术/位运算（补码回绕 == C# unchecked） ----

    @Test
    void arithmeticUInt32_matchesWraparoundSemantics() {
        assertEquals(-2, UnsignedMath.addUInt32(Integer.MAX_VALUE, Integer.MAX_VALUE));
        assertEquals(0, UnsignedMath.subtractUInt32(Integer.MIN_VALUE, Integer.MIN_VALUE));
        assertEquals(0x40000000, UnsignedMath.multiplyUInt32(0x40000000, 5));
        assertEquals(-4, UnsignedMath.xorUInt32(-1, 3));
        assertEquals(0x00F0, UnsignedMath.andUInt32(0xF0F0, 0x0FF0));
        assertEquals(-1, UnsignedMath.orUInt32(-8, 7));
        assertEquals(0, UnsignedMath.notUInt32(-1));
    }

    @Test
    void arithmeticUInt64_matchesWraparoundSemantics() {
        assertEquals(-2L, UnsignedMath.addUInt64(Long.MAX_VALUE, Long.MAX_VALUE));
        assertEquals(0x4000000000000000L, UnsignedMath.multiplyUInt64(0x4000000000000000L, 5L));
        assertEquals(~(-1L), UnsignedMath.notUInt64(-1L));
        assertEquals(-1L, UnsignedMath.orUInt64(-8L, 7L));
    }

    @Test
    void divideRemainderUInt32_matchesJdkReference() {
        int[] divisors = {1, 2, 3, 17, Integer.MAX_VALUE, -1, -2, Integer.MIN_VALUE};
        for (int a : INT_EDGE) {
            for (int b : divisors) {
                assertEquals(Integer.divideUnsigned(a, b), UnsignedMath.divideUInt32(a, b),
                        "divide(" + a + ", " + b + ")");
                assertEquals(Integer.remainderUnsigned(a, b), UnsignedMath.remainderUInt32(a, b),
                        "remainder(" + a + ", " + b + ")");
            }
        }
    }

    @Test
    void divideRemainderUInt64_matchesJdkReference() {
        long[] divisors = {1L, 2L, 3L, 17L, Long.MAX_VALUE, -1L, -2L, Long.MIN_VALUE};
        for (long a : LONG_EDGE) {
            for (long b : divisors) {
                assertEquals(Long.divideUnsigned(a, b), UnsignedMath.divideUInt64(a, b),
                        "divide(" + a + ", " + b + ")");
                assertEquals(Long.remainderUnsigned(a, b), UnsignedMath.remainderUInt64(a, b),
                        "remainder(" + a + ", " + b + ")");
            }
        }
    }

    // ---- 比较 ----

    @Test
    void compare_matchesJdkReference() {
        for (int a : INT_EDGE) {
            for (int b : INT_EDGE) {
                assertEquals(Integer.compareUnsigned(a, b), UnsignedMath.compareUInt32(a, b));
            }
        }
        for (long a : LONG_EDGE) {
            for (long b : LONG_EDGE) {
                assertEquals(Long.compareUnsigned(a, b), UnsignedMath.compareUInt64(a, b));
                assertEquals(Long.compare(a, b), UnsignedMath.compareInt64(a, b));
            }
        }
    }

    // ---- 转换（与 C# 版逐方法对齐） ----

    @Test
    void conversions_matchCSharpSemantics() {
        assertEquals(0xFF, UnsignedMath.toByteInt32(0x1234ABFF));
        assertEquals(0xFF, UnsignedMath.toByteInt64(0x12345678ABCDEFFL));
        assertEquals(0xCDEF, UnsignedMath.toUInt16Int32(0x1234CDEF));
        assertEquals(0xCDEF, UnsignedMath.toUInt16Int64(0x12345678ABCDEFL));
        assertEquals(4294967295L, UnsignedMath.toInt64UInt32(-1));
        assertEquals(4294967295L, UnsignedMath.toUInt64UInt32(-1));
        assertEquals(-1, UnsignedMath.toInt32UInt64(-1L));
        assertEquals(4294967295.0, UnsignedMath.toDoubleUInt32(-1), 0.0);
        assertEquals(1.8446744073709552E19, UnsignedMath.toDoubleUInt64(-1L), 0.0);
        assertEquals(4.2949673E9f, UnsignedMath.toSingleUInt32(-1), 0.0f);
        assertEquals(1.8446744E19f, UnsignedMath.toSingleUInt64(-1L), 0.0f);
        assertEquals(Decimal.parse("4294967295"), UnsignedMath.toDecimalUInt32(-1));
        assertEquals(Decimal.parse("18446744073709551615"), UnsignedMath.toDecimalUInt64(-1L));
    }

    // ---- ToString（unsigned） ----

    @Test
    void toStringUnsigned_plainOverloads() {
        assertEquals("0", UnsignedMath.toStringUInt32(0));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1));
        assertEquals("2147483647", UnsignedMath.toStringUInt32(Integer.MAX_VALUE));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L));
        assertEquals("0", UnsignedMath.toStringUInt64(0L));
        // provider 被忽略，恒 InvariantCulture（与 C# 版一致）
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, CultureInfo.getInvariantCulture()));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L, CultureInfo.getInvariantCulture()));
    }

    @Test
    void toStringUInt32_formatOverloads() {
        assertEquals("FF", UnsignedMath.toStringUInt32(255, "X"));
        assertEquals("00ff", UnsignedMath.toStringUInt32(255, "x4"));
        assertEquals("00012345", UnsignedMath.toStringUInt32(12345, "D8"));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, ""));
        assertEquals("4294967295", UnsignedMath.toStringUInt32(-1, (String) null));
        // 带格式 + provider：provider 恒 Invariant
        assertEquals("FF", UnsignedMath.toStringUInt32(255, "X", CultureInfo.getInvariantCulture()));
        assertEquals("FFFFFFFF", UnsignedMath.toStringUInt32(-1, "X", null));
    }

    @Test
    void toStringUInt64_formatOverloads() {
        assertEquals("FFFFFFFFFFFFFFFF", UnsignedMath.toStringUInt64(-1L, "X"));
        assertEquals("ffffffffffffffff", UnsignedMath.toStringUInt64(-1L, "x"));
        assertEquals("00000000000000ff", UnsignedMath.toStringUInt64(255L, "x16"));
        assertEquals("001", UnsignedMath.toStringUInt64(1L, "D3"));
        assertEquals("18446744073709551615", UnsignedMath.toStringUInt64(-1L, ""));
        assertEquals("FFFFFFFFFFFFFFFF", UnsignedMath.toStringUInt64(-1L, "X", CultureInfo.getInvariantCulture()));
    }

    // ---- 闭集外但全集口径内的对称方法（编译冒烟 + 基本行为） ----

    @Test
    void int32Domain_andShifts_compileAndBehave() {
        assertEquals(-2, UnsignedMath.addInt32(Integer.MAX_VALUE, Integer.MAX_VALUE));
        assertEquals(2, UnsignedMath.subtractInt32(Integer.MAX_VALUE, Integer.MAX_VALUE - 2));
        assertEquals(-1, UnsignedMath.notInt32(0));
        assertEquals(1 << 5, UnsignedMath.leftShiftInt32(1, 5));
        assertEquals(-16 >> 2, UnsignedMath.rightShiftInt32(-16, 2));
        assertEquals(Integer.compare(-3, 2), UnsignedMath.compareInt32(-3, 2));
        assertEquals(-1 >>> 3, UnsignedMath.rightShiftUInt32(-1, 3));
        assertEquals(-1L >>> 63, UnsignedMath.rightShiftUInt64(-1L, 63));
        assertEquals(1L << 62, UnsignedMath.leftShiftUInt64(1L, 62));
    }

    @Test
    void int64Domain_signedOps_behave() {
        assertEquals(-2L, UnsignedMath.addInt64(Long.MAX_VALUE, Long.MAX_VALUE));
        assertEquals(6L, UnsignedMath.multiplyInt64(-3L, -2L));
        assertEquals(-3L / 2L, UnsignedMath.divideInt64(-3L, 2L));
        assertEquals(-3L % 2L, UnsignedMath.remainderInt64(-3L, 2L));
        assertEquals(0xFL, UnsignedMath.andInt64(0xFFL, 0x0FL));
        assertEquals(0xF0L, UnsignedMath.orInt64(0xF0L, 0x00L));
        assertEquals(0xFFL ^ 0x0FL, UnsignedMath.xorInt64(0xFFL, 0x0FL));
    }

    // ---- checked 算术（C15 numeric-boundary；溢出断言 ArithmeticException） ----

    @Test
    void checkedInt32_inRange_matchesExactMath() {
        assertEquals(5, UnsignedMath.addCheckedInt32(2, 3));
        assertEquals(-1, UnsignedMath.subtractCheckedInt32(2, 3));
        assertEquals(-6, UnsignedMath.multiplyCheckedInt32(-2, 3));
        assertEquals(7, UnsignedMath.negateCheckedInt32(-7));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.addCheckedInt32(Integer.MAX_VALUE, 1));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.subtractCheckedInt32(Integer.MIN_VALUE, 1));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.multiplyCheckedInt32(Integer.MAX_VALUE, 2));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.negateCheckedInt32(Integer.MIN_VALUE));
    }

    @Test
    void checkedInt64_inRangeAndOverflow() {
        assertEquals(5L, UnsignedMath.addCheckedInt64(2L, 3L));
        assertEquals(-4L, UnsignedMath.negateCheckedInt64(4L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.addCheckedInt64(Long.MAX_VALUE, 1L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.subtractCheckedInt64(Long.MIN_VALUE, 1L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.multiplyCheckedInt64(Long.MAX_VALUE, 2L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.negateCheckedInt64(Long.MIN_VALUE));
    }

    @Test
    void checkedUInt32_inRangeAndOverflow() {
        assertEquals(5, UnsignedMath.addCheckedUInt32(2, 3));
        assertEquals(1, UnsignedMath.subtractCheckedUInt32(3, 2));
        assertEquals(12, UnsignedMath.multiplyCheckedUInt32(3, 4));
        // 0xFFFFFFFF + 1 / 0 - 1 / 0xFFFFFFFF * 2 溢出
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.addCheckedUInt32(-1, 1));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.subtractCheckedUInt32(0, 1));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.multiplyCheckedUInt32(-1, 2));
        // 2^32 - 1 + 0 不溢出
        assertEquals(-1, UnsignedMath.addCheckedUInt32(-1, 0));
        assertEquals(-1, UnsignedMath.subtractCheckedUInt32(-1, 0));
    }

    @Test
    void checkedUInt64_inRangeAndOverflow() {
        assertEquals(5L, UnsignedMath.addCheckedUInt64(2L, 3L));
        assertEquals(1L, UnsignedMath.subtractCheckedUInt64(3L, 2L));
        assertEquals(12L, UnsignedMath.multiplyCheckedUInt64(3L, 4L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.addCheckedUInt64(-1L, 1L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.subtractCheckedUInt64(0L, 1L));
        assertThrows(ArithmeticException.class,
                () -> UnsignedMath.multiplyCheckedUInt64(-1L, 2L));
        // 边界不溢出：max + 0、max - max
        assertEquals(-1L, UnsignedMath.addCheckedUInt64(-1L, 0L));
        assertEquals(0L, UnsignedMath.subtractCheckedUInt64(-1L, -1L));
    }

    // ---- checked 转换族（C15；越界断言 ArithmeticException） ----

    @Test
    void checkedConversions_inRangeReturnSameValue() {
        assertEquals(127, UnsignedMath.toSByteCheckedInt32(127));
        assertEquals(255, UnsignedMath.toByteCheckedInt32(255));
        assertEquals(32767, UnsignedMath.toInt16CheckedInt32(32767));
        assertEquals(65535, UnsignedMath.toUInt16CheckedInt32(65535));
        assertEquals(65535, UnsignedMath.toCharCheckedInt32(65535));
        assertEquals(0x7FFFFFFF, UnsignedMath.toUInt32CheckedInt32(Integer.MAX_VALUE));
        assertEquals(0L, UnsignedMath.toUInt64CheckedInt32(0));
        assertEquals(Integer.MAX_VALUE, UnsignedMath.toUInt64CheckedInt32(Integer.MAX_VALUE));

        assertEquals(255, UnsignedMath.toByteCheckedUInt32(255));
        assertEquals(0x7FFFFFFF, UnsignedMath.toInt32CheckedUInt32(0x7FFFFFFF));

        assertEquals(127L, UnsignedMath.toSByteCheckedInt64(127L));
        // Java stores uint32 in int: compare through the unsigned view.
        assertEquals(0xFFFFFFFFL, Integer.toUnsignedLong(UnsignedMath.toUInt32CheckedInt64(0xFFFFFFFFL)));
        assertEquals(Long.MAX_VALUE, UnsignedMath.toUInt64CheckedInt64(Long.MAX_VALUE));

        assertEquals(255, UnsignedMath.toByteCheckedUInt64(255L));
        assertEquals(0x7FFFFFFFL, UnsignedMath.toInt32CheckedUInt64(0x7FFFFFFFL));
        assertEquals(Long.MAX_VALUE, UnsignedMath.toInt64CheckedUInt64(Long.MAX_VALUE));
    }

    @Test
    void checkedConversions_overflowThrows() {
        // from Int32
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toSByteCheckedInt32(128));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toByteCheckedInt32(-1));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toInt16CheckedInt32(32768));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toUInt16CheckedInt32(-1));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toCharCheckedInt32(65536));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toUInt32CheckedInt32(-1));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toUInt64CheckedInt32(-1));

        // from UInt32（参数为无符号存储 int）
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toSByteCheckedUInt32(128));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toInt32CheckedUInt32(-1));

        // from Int64
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toInt32CheckedInt64(0x1_00000000L));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toUInt32CheckedInt64(-1L));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toUInt64CheckedInt64(-1L));

        // from UInt64（参数为无符号存储 long）
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toByteCheckedUInt64(256L));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toInt32CheckedUInt64(-1L));
        assertThrows(ArithmeticException.class, () -> UnsignedMath.toInt64CheckedUInt64(-1L));
    }
}
