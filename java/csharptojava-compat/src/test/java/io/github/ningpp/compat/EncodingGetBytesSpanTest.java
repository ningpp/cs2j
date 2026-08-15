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
}
