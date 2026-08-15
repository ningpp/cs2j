package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class ReadOnlySpanNullArrayTest {

    @Test
    void nullArray_YieldsEmptySpan() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>((Integer[]) null);
        assertTrue(span.isEmpty());
        assertEquals(0, span.length());
    }

    @Test
    void nullByteArray_YieldsEmptySpan() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>((byte[]) null);
        assertTrue(span.isEmpty());
        assertEquals(0, span.length());
    }

    @Test
    void nullArrayWithZeroSlice_YieldsEmptySpan() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>((Integer[]) null, 0, 0);
        assertTrue(span.isEmpty());
    }
}
