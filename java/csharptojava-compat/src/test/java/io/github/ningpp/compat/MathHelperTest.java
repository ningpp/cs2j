package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

/**
 * Tests for MathHelper numeric parsing methods.
 */
class MathHelperTest {

    // ---- tryParseInt ----

    @Test
    void tryParseInt_validPositive() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseInt("123", holder));
        assertEquals(123, holder.value);
    }

    @Test
    void tryParseInt_validNegative() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseInt("-42", holder));
        assertEquals(-42, holder.value);
    }

    @Test
    void tryParseInt_invalid() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseInt("abc", holder));
        // value should remain unchanged (default 0)
        assertEquals(0, holder.value);
    }

    @Test
    void tryParseInt_empty() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseInt("", holder));
    }

    @Test
    void tryParseInt_zero() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseInt("0", holder));
        assertEquals(0, holder.value);
    }

    @Test
    void tryParseInt_maxValue() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseInt(String.valueOf(Integer.MAX_VALUE), holder));
        assertEquals(Integer.MAX_VALUE, holder.value);
    }

    @Test
    void tryParseInt_overflow() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseInt("99999999999999", holder));
    }

    @Test
    void tryParseInt_decimalString() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseInt("3.14", holder));
    }

    // ---- tryParseLong ----

    @Test
    void tryParseLong_valid() {
        LongHolder holder = new LongHolder();
        assertTrue(MathHelper.tryParseLong("999999999999", holder));
        assertEquals(999999999999L, holder.value);
    }

    @Test
    void tryParseLong_invalid() {
        LongHolder holder = new LongHolder();
        assertFalse(MathHelper.tryParseLong("notanumber", holder));
        assertEquals(0L, holder.value);
    }

    @Test
    void tryParseLong_negative() {
        LongHolder holder = new LongHolder();
        assertTrue(MathHelper.tryParseLong("-100", holder));
        assertEquals(-100L, holder.value);
    }

    @Test
    void tryParseLong_maxValue() {
        LongHolder holder = new LongHolder();
        assertTrue(MathHelper.tryParseLong(String.valueOf(Long.MAX_VALUE), holder));
        assertEquals(Long.MAX_VALUE, holder.value);
    }

    // ---- unsigned integer parsing ----

    @Test
    void tryParseUShort_acceptsMaxValueIntoIntHolder() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseUShort(" 65535 ", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
        assertEquals(65535, holder.value);
    }

    @Test
    void tryParseUShort_rejectsOverflow() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseUShort("65536", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
    }

    @Test
    void tryParseUInt_acceptsMaxValueIntoIntHolder() {
        IntHolder holder = new IntHolder();
        assertTrue(MathHelper.tryParseUInt("4294967295", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
        assertEquals(-1, holder.value);
    }

    @Test
    void tryParseUInt_rejectsOverflow() {
        IntHolder holder = new IntHolder();
        assertFalse(MathHelper.tryParseUInt("4294967296", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
    }

    @Test
    void tryParseULong_acceptsMaxValueIntoLongHolder() {
        LongHolder holder = new LongHolder();
        assertTrue(MathHelper.tryParseULong("18446744073709551615", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
        assertEquals(-1L, holder.value);
    }

    @Test
    void tryParseULong_rejectsOverflow() {
        LongHolder holder = new LongHolder();
        assertFalse(MathHelper.tryParseULong("18446744073709551616", NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.getInvariantCulture(), holder));
    }

    // ---- tryParseDouble ----

    @Test
    void tryParseDouble_valid() {
        DoubleHolder holder = new DoubleHolder();
        assertTrue(MathHelper.tryParseDouble("3.14", holder));
        assertEquals(3.14, holder.value, 0.0001);
    }

    @Test
    void tryParseDouble_invalid() {
        DoubleHolder holder = new DoubleHolder();
        assertFalse(MathHelper.tryParseDouble("xyz", holder));
        assertEquals(0.0, holder.value);
    }

    @Test
    void tryParseDouble_negative() {
        DoubleHolder holder = new DoubleHolder();
        assertTrue(MathHelper.tryParseDouble("-2.5", holder));
        assertEquals(-2.5, holder.value, 0.0001);
    }

    @Test
    void tryParseDouble_integer() {
        DoubleHolder holder = new DoubleHolder();
        assertTrue(MathHelper.tryParseDouble("42", holder));
        assertEquals(42.0, holder.value, 0.0001);
    }

    @Test
    void tryParseDouble_scientificNotation() {
        DoubleHolder holder = new DoubleHolder();
        assertTrue(MathHelper.tryParseDouble("1.5e10", holder));
        assertEquals(1.5e10, holder.value, 1000);
    }

    @Test
    void tryParseDouble_empty() {
        DoubleHolder holder = new DoubleHolder();
        assertFalse(MathHelper.tryParseDouble("", holder));
    }

    // ---- tryParseFloat ----

    @Test
    void tryParseFloat_valid() {
        FloatHolder holder = new FloatHolder();
        assertTrue(MathHelper.tryParseFloat("2.5", holder));
        assertEquals(2.5f, holder.value, 0.0001f);
    }

    @Test
    void tryParseFloat_invalid() {
        FloatHolder holder = new FloatHolder();
        assertFalse(MathHelper.tryParseFloat("abc", holder));
        assertEquals(0.0f, holder.value);
    }

    @Test
    void tryParseFloat_negative() {
        FloatHolder holder = new FloatHolder();
        assertTrue(MathHelper.tryParseFloat("-1.5", holder));
        assertEquals(-1.5f, holder.value, 0.0001f);
    }

    // ---- tryParseBool ----

    @Test
    void tryParseBool_true() {
        BoolHolder holder = new BoolHolder();
        assertTrue(MathHelper.tryParseBool("true", holder));
        assertTrue(holder.value);
    }

    @Test
    void tryParseBool_false() {
        BoolHolder holder = new BoolHolder();
        assertTrue(MathHelper.tryParseBool("false", holder));
        assertFalse(holder.value);
    }

    @Test
    void tryParseBool_caseInsensitive_true() {
        BoolHolder holder = new BoolHolder();
        assertTrue(MathHelper.tryParseBool("TRUE", holder));
        assertTrue(holder.value);
    }

    @Test
    void tryParseBool_caseInsensitive_mixed() {
        BoolHolder holder = new BoolHolder();
        assertTrue(MathHelper.tryParseBool("True", holder));
        assertTrue(holder.value);
    }

    @Test
    void tryParseBool_caseInsensitive_false() {
        BoolHolder holder = new BoolHolder();
        assertTrue(MathHelper.tryParseBool("FALSE", holder));
        assertFalse(holder.value);
    }

    @Test
    void tryParseBool_invalid() {
        BoolHolder holder = new BoolHolder();
        assertFalse(MathHelper.tryParseBool("yes", holder));
        assertFalse(MathHelper.tryParseBool("1", holder));
        assertFalse(MathHelper.tryParseBool("", holder));
    }

    // ---- tryFormatByte ----

    @Test
    void tryFormatByte_default() {
        Character[] arr = new Character[10];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatByte((byte)255, dest, written, null));
        assertEquals(3, written.value);
        assertEquals('2', dest.get(0));
        assertEquals('5', dest.get(1));
        assertEquals('5', dest.get(2));
    }

    @Test
    void tryFormatByte_hexFormat() {
        Character[] arr = new Character[10];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatByte((byte)255, dest, written, "X2"));
        assertEquals(2, written.value);
        assertEquals('F', dest.get(0));
        assertEquals('F', dest.get(1));
    }

    @Test
    void tryFormatByte_bufferTooSmall() {
        Character[] arr = new Character[1];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertFalse(MathHelper.tryFormatByte((byte)255, dest, written, null));
    }

    // ---- tryFormatInt ----

    @Test
    void tryFormatInt_default() {
        Character[] arr = new Character[20];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatInt(42, dest, written, null));
        assertEquals(2, written.value);
        assertEquals('4', dest.get(0));
        assertEquals('2', dest.get(1));
    }

    @Test
    void tryFormatInt_hexFormat() {
        Character[] arr = new Character[20];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatInt(255, dest, written, "X8"));
        assertEquals(8, written.value);
        assertEquals('0', dest.get(0));
        assertEquals('F', dest.get(7));
    }

    @Test
    void tryFormatInt_negative() {
        Character[] arr = new Character[20];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatInt(-1, dest, written, null));
        assertEquals(2, written.value);
        assertEquals('-', dest.get(0));
        assertEquals('1', dest.get(1));
    }

    // ---- tryFormatDouble ----

    @Test
    void tryFormatDouble_default() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatDouble(3.14, dest, written, null));
        assertTrue(written.value > 0);
    }

    @Test
    void tryFormatDouble_fixedPoint() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatDouble(3.14159, dest, written, "F2"));
        assertEquals(4, written.value);
        assertEquals('3', dest.get(0));
        assertEquals('.', dest.get(1));
        assertEquals('1', dest.get(2));
        assertEquals('4', dest.get(3));
    }

    // ---- tryFormatLong ----

    @Test
    void tryFormatLong_default() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatLong(123456789L, dest, written, null));
        assertEquals(9, written.value);
    }

    // ---- tryFormatFloat ----

    @Test
    void tryFormatFloat_fixedPoint() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatFloat(2.5f, dest, written, "F1"));
        assertEquals(3, written.value);
        assertEquals('2', dest.get(0));
        assertEquals('.', dest.get(1));
        assertEquals('5', dest.get(2));
    }

    // ---- tryFormatShort ----

    @Test
    void tryFormatShort_default() {
        Character[] arr = new Character[10];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatShort((short)100, dest, written, null));
        assertEquals(3, written.value);
    }

    // ---- tryFormatUInt (unsigned int via long) ----

    @Test
    void tryFormatUInt_default() {
        Character[] arr = new Character[20];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatUInt(Integer.MAX_VALUE + 1L, dest, written, null));
        assertTrue(written.value > 0);
    }

    // ---- tryFormatULong (unsigned long via BigInteger) ----

    @Test
    void tryFormatULong_default() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatULong(-1L, dest, written, null));
        assertEquals(20, written.value);
    }

    // ---- tryFormatSByte ----

    @Test
    void tryFormatSByte_default() {
        Character[] arr = new Character[10];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatSByte((byte)-1, dest, written, null));
        assertEquals(2, written.value);
        assertEquals('-', dest.get(0));
        assertEquals('1', dest.get(1));
    }

    // ---- tryFormatUShort ----

    @Test
    void tryFormatUShort_default() {
        Character[] arr = new Character[10];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatUShort((short)65535, dest, written, null));
        assertEquals(5, written.value);
    }

    // ---- tryFormatDecimal ----

    @Test
    void tryFormatDecimal_fixedPoint() {
        Character[] arr = new Character[30];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatDecimal(Decimal.parse("123.45"), dest, written, "F2"));
        assertEquals(6, written.value);
        assertEquals('1', dest.get(0));
    }

    // ---- formatNumeric ----

    @Test
    void formatNumeric_hexUppercase() {
        assertEquals("EF", MathHelper.formatNumeric("X2", 0xEF));
    }

    @Test
    void formatNumeric_hexLowercase() {
        assertEquals("ef", MathHelper.formatNumeric("x2", 0xEF));
    }

    @Test
    void formatNumeric_hexWithPadding() {
        assertEquals("00EF", MathHelper.formatNumeric("X4", 0xEF));
    }

    @Test
    void formatNumeric_decimalWithPadding() {
        assertEquals("00000042", MathHelper.formatNumeric("D8", 42));
    }

    @Test
    void formatNumeric_fixedPoint() {
        assertEquals("3.14", MathHelper.formatNumeric("F2", 3.14));
    }

    @Test
    void formatNumeric_nullFormat() {
        assertEquals("42", MathHelper.formatNumeric(null, 42));
    }

    @Test
    void formatNumeric_emptyFormat() {
        assertEquals("42", MathHelper.formatNumeric("", 42));
    }

    @Test
    void formatNumeric_byteValueWithMask() {
        // Simulates C# byte.ToString("X2") where byte is unsigned (0-255)
        // In Java, a byte value of -17 (0xEF) should format as "EF" when masked with & 0xFF
        int unsignedByteValue = ((int)(byte)0xEF) & 0xFF;
        assertEquals("EF", MathHelper.formatNumeric("X2", unsignedByteValue));
    }

    @Test
    void formatNumeric_scientific() {
        String result = MathHelper.formatNumeric("E2", 1234.5);
        assertTrue(result.startsWith("1.23")); // 1.23E+03 or similar
    }

    // ---- tryFormatObject ----

    @Test
    void tryFormatObject_string() {
        Character[] arr = new Character[20];
        Span<Character> dest = new Span<>(arr);
        IntHolder written = new IntHolder();
        assertTrue(MathHelper.tryFormatObject("hello", dest, written, null));
        assertEquals(5, written.value);
        assertEquals('h', dest.get(0));
    }
}
