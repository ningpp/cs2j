package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import org.junit.jupiter.api.Test;

class NullableTest {

    @Test
    void value_presentReturnsUnboxed() {
        assertEquals(true, Nullable.value(Boolean.TRUE));
        assertEquals(42, Nullable.value(Integer.valueOf(42)));
        assertEquals(42L, Nullable.value(Long.valueOf(42L)));
        assertEquals((short) 42, Nullable.value(Short.valueOf((short) 42)));
        assertEquals((byte) 42, Nullable.value(Byte.valueOf((byte) 42)));
        assertEquals('A', Nullable.value(Character.valueOf('A')));
        assertEquals(1.5f, Nullable.value(Float.valueOf(1.5f)));
        assertEquals(1.5d, Nullable.value(Double.valueOf(1.5d)));
    }

    @Test
    void value_nullThrowsIllegalStateLikeCSharpNullable() {
        // C# Nullable<T>.Value throws InvalidOperationException, mapped to
        // IllegalStateException (never NullPointerException).
        assertThrows(IllegalStateException.class, () -> Nullable.value((Integer) null));
        assertThrows(IllegalStateException.class, () -> Nullable.value((Boolean) null));
        assertThrows(IllegalStateException.class, () -> Nullable.value((Double) null));
    }
}
