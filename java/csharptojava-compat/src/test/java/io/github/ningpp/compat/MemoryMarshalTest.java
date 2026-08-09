package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.*;

class MemoryMarshalTest {
    @Test
    void asBytesReturnsUnsignedByteSpanCompatibleWithConvertedReadOnlySpanByte() {
        ReadOnlySpan<Integer> bytes = MemoryMarshal.asBytes(new ReadOnlySpan<>(new Integer[] { 65 }));

        assertEquals(4, bytes.length());
        assertEquals(0, bytes.get(0));
        assertEquals(0, bytes.get(1));
        assertEquals(0, bytes.get(2));
        assertEquals(65, bytes.get(3));
    }

    @Test
    void asBytesReinterpretsCharacterSpanAsUtf16LittleEndianBytes() {
        ReadOnlySpan<Character> chars = new ReadOnlySpan<>(new Character[] { 'A' });

        ReadOnlySpan<Integer> bytes = MemoryMarshal.asBytes(chars);

        assertEquals(2, bytes.length());
        assertEquals(0x41, bytes.get(0));
        assertEquals(0x00, bytes.get(1));
    }
}
