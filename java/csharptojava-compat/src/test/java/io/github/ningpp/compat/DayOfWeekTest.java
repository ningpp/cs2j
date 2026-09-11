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
}
