package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class EncodingGetBytesSpanTest {
    @Test
    void getBytesWritesIntoByteBackedSpan() {
        Encoding utf8 = Encoding.getUTF8();
        ReadOnlySpan<Character> chars = new ReadOnlySpan<Character>(
            new Character[] { 'H', 'e', 'l', 'l', 'o' });
        Span<Integer> dest = new Span<>(new byte[10]);

        int written = utf8.getBytes(chars, dest);

        assertEquals(5, written);
        assertEquals(72, dest.get(0));
        assertEquals(101, dest.get(1));
        assertEquals(108, dest.get(2));
        assertEquals(108, dest.get(3));
        assertEquals(111, dest.get(4));
    }

    @Test
    void getBytesWritesStringBackedSpan() {
        Encoding utf8 = Encoding.getUTF8();
        ReadOnlySpan<Character> chars = MemoryExtensions.asSpan("Hello");
        Span<Integer> dest = new Span<>(new byte[10]);

        int written = utf8.getBytes(chars, dest);

        assertEquals(5, written);
        assertEquals(72, dest.get(0));
        assertEquals(101, dest.get(1));
        assertEquals(108, dest.get(2));
        assertEquals(108, dest.get(3));
        assertEquals(111, dest.get(4));
    }

    @Test
    void getBytesFromStringReturnsExpectedBytes() {
        Encoding utf8 = Encoding.getUTF8();
        byte[] bytes = utf8.getBytes("NetworkCodeList");

        assertEquals(15, bytes.length);
        assertEquals(78, bytes[0] & 0xFF);
        assertEquals(101, bytes[1] & 0xFF);
        assertEquals(116, bytes[2] & 0xFF);
    }

    @Test
    void asSpanOverByteSliceIsAView() {
        byte[] array = new byte[] { 1, 2, 3, 4, 5 };
        ReadOnlySpan<Integer> span = MemoryExtensions.asSpan(array, 1, 3);

        new Span<>(span).set(0, 99);

        assertEquals(99, array[1]);
        assertEquals(3, array[2]);
        assertEquals(4, array[3]);
    }
}
