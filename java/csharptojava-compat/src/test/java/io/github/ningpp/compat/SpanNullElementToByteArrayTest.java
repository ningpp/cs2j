package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

import org.junit.jupiter.api.Test;

/// JsonWriterHelper.toUtf8 materializes the full destination span before writing;
/// unwritten tail elements are null in the boxed Integer representation and must
/// convert to zero bytes instead of throwing NPE.
class SpanNullElementToByteArrayTest {
    @Test
    void spanToByteArrayTreatsNullElementsAsZero() {
        Span<Integer> span = new Span<>(new Integer[] { 65, null, 67 });
        assertArrayEquals(new byte[] { 65, 0, 67 }, span.toByteArray());
    }

    @Test
    void readOnlySpanToByteArrayTreatsNullElementsAsZero() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>(new Integer[] { 65, null, 67 });
        assertArrayEquals(new byte[] { 65, 0, 67 }, span.toByteArray());
    }
}
