package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class MemoryMarshalTest {
    @Test
    void asBytesReturnsUnsignedByteSpanCompatibleWithConvertedReadOnlySpanByte() {
        ReadOnlySpan<Integer> bytes = MemoryMarshal.asBytes(MemoryExtensions.asSpan("A"));

        assertEquals(4, bytes.length());
        assertEquals(0, bytes.get(0));
        assertEquals(0, bytes.get(1));
        assertEquals(0, bytes.get(2));
        assertEquals(65, bytes.get(3));
    }
}
