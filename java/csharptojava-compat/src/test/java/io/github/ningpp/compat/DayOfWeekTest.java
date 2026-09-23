package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

/**
 * C# {@code System.DayOfWeek} has seven named constants but a DayOfWeek-typed
 * variable may legally hold any underlying int (e.g. {@code (DayOfWeek)(-1)}), so the
 * compat stand-in must mirror the converter's enum shape: an {@code _UNMAPPED}
 * sentinel that round-trips the raw value instead of indexing {@code values()}.
 */
class DayOfWeekTest {

    @Test
    void fromValue_namedValues_returnConstants() {
        assertSame(DayOfWeek.Sunday, DayOfWeek.fromValue(0));
        assertSame(DayOfWeek.Monday, DayOfWeek.fromValue(1));
        assertSame(DayOfWeek.Saturday, DayOfWeek.fromValue(6));
        assertEquals(6, DayOfWeek.fromValue(6).getValue());
    }

    @Test
    void fromValue_negativeValue_returnsSentinelPreservingRawValue() {
        DayOfWeek value = assertDoesNotThrow(() -> DayOfWeek.fromValue(-1));
        assertSame(DayOfWeek._UNMAPPED, value);
        assertEquals(-1, value.getValue());
        assertEquals(-1, EnumHelper.toInt32(value));
    }

    @Test
    void fromValue_valueOutsideNamedRange_preservesRawValue() {
        assertEquals(42, DayOfWeek.fromValue(42).getValue());
        assertEquals(-7, DayOfWeek.fromValue(-7).getValue());
    }

    @Test
    void sentinelIsExcludedFromNamesAndValues() {
        assertEquals(7, EnumHelper.getNames(DayOfWeek.class).length);
        assertEquals(7, EnumHelper.getValuesArray(DayOfWeek.class).length);
        for (String name : EnumHelper.getNames(DayOfWeek.class)) {
            assertNotEquals("_UNMAPPED", name);
        }
        for (Object constant : EnumHelper.getValuesArray(DayOfWeek.class)) {
            assertNotSame(DayOfWeek._UNMAPPED, constant);
        }
    }

    @Test
    void fromJava_mapsJavaDayOfWeekAndToleratesNull() {
        assertSame(DayOfWeek.Monday, DayOfWeek.fromJava(java.time.DayOfWeek.MONDAY));
        assertSame(DayOfWeek.Sunday, DayOfWeek.fromJava(java.time.DayOfWeek.SUNDAY));
        assertNull(DayOfWeek.fromJava(null));
    }

    @Test
    void toString_definedValues_returnConstantNames() {
        assertEquals("Sunday", DayOfWeek.Sunday.toString());
        assertEquals("Monday", DayOfWeek.Monday.toString());
        assertEquals("Saturday", DayOfWeek.Saturday.toString());
    }

    /**
     * C# {@code Enum.ToString()} renders an undefined value as its underlying
     * number ({@code ((DayOfWeek)(-1)).ToString() == "-1"}), never as a synthetic
     * identifier. The generated {@code EnumConverter.write} leans on exactly this:
     * it calls {@code value.toString()}, treats a leading letter as "write as
     * string", and otherwise falls back to the numeric value from
     * {@code Unsafe.as} — so a sentinel that stringifies as {@code "_UNMAPPED"}
     * would leak into JSON as {@code "_unmapped"} instead of {@code -1}
     * (SDK test EnumConverterTests.convertDayOfWeek).
     */
    @Test
    void toString_sentinel_returnsUnderlyingNumberLikeCSharp() {
        assertEquals("-1", DayOfWeek.fromValue(-1).toString());
        assertEquals("42", DayOfWeek.fromValue(42).toString());
        assertEquals("-7", DayOfWeek.fromValue(-7).toString());
    }

    /**
     * The full seam contract the generated EnumConverter.write implements for an
     * undefined value: numeric cache key miss, non-identifier toString (C# renders
     * the number), then the numeric fallback reading the same unmapped value.
     */
    @Test
    @SuppressWarnings("unchecked")
    void undefinedValue_followsGeneratedEnumConverterWritePath() {
        Object sentinel = DayOfWeek.fromValue(-1);
        assertEquals(-1L, EnumHelper.toInt32(sentinel)); // convertToUInt64 key: cache miss
        String original = sentinel.toString();           // C# Enum.ToString()
        assertEquals("-1", original);
        assertFalse(original.charAt(0) >= 'A', "must not look like an identifier"); // isValidIdentifier
        assertEquals(-1, ((Integer) io.github.ningpp.compat.Unsafe.as(sentinel)).intValue()); // numeric write
    }
}
