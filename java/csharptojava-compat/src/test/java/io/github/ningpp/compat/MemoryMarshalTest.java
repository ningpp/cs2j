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

    /**
     * C# MemoryMarshal.Read&lt;int&gt;(byteSpan) reads a primitive; the converted
     * call site passes int.class (JsonDocument.MetadataDb.getJsonTokenType reads
     * its packed union this way). The value-class/reflective branches return null
     * for primitives, which surfaced as an NPE at the unboxing site.
     */
    @Test
    void readPrimitiveFromByteSpan() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>(
                new Integer[] {0x78, 0x56, 0x34, 0x12, 0x00, 0x00, 0x00, 0x00});

        assertEquals(0x12345678, MemoryMarshal.read(span, int.class));
        assertEquals((short) 0x5678, MemoryMarshal.read(span, short.class));
        assertEquals((byte) 0x78, MemoryMarshal.read(span, byte.class));
        assertEquals(0x12345678L, MemoryMarshal.read(span, long.class));
        assertEquals(Boolean.TRUE, MemoryMarshal.read(span, boolean.class));
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
