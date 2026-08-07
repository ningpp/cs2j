package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * Verifies the Character-based Span/MemoryExtensions surface required by the
 * converted ValueStringBuilder: asSpan(String) must yield ReadOnlySpan&lt;Character&gt;
 * (copyable into Span&lt;Character&gt;) and Span must support tryCopyTo(Span&lt;T&gt;).
 */
public class SpanCharCompatTest {

    @Test
    public void asSpanString_returnsCharacterSpan_copyableIntoMutableSpan() {
        ReadOnlySpan<Character> src = MemoryExtensions.asSpan("abcd");
        assertEquals(4, src.length());
        assertEquals(Character.valueOf('a'), src.get(0));

        Character[] dest = new Character[4];
        Span<Character> destSpan = new Span<>(dest);
        src.copyTo(destSpan);
        assertEquals(Character.valueOf('d'), destSpan.get(3));
    }

    @Test
    public void asSpanStringSlice_returnsCharacterSpan() {
        ReadOnlySpan<Character> slice = MemoryExtensions.asSpan("abcdef", 1, 3);
        assertEquals(3, slice.length());
        assertEquals(Character.valueOf('b'), slice.get(0));
        assertEquals(Character.valueOf('d'), slice.get(2));
    }

    @Test
    public void asSpanCharArray_returnsCharacterSpan() {
        ReadOnlySpan<Character> src = MemoryExtensions.asSpan(new char[] { 'x', 'y' });
        assertEquals(2, src.length());
        assertEquals(Character.valueOf('y'), src.get(1));
    }

    @Test
    public void spanTryCopyToSpan_copiesAndReportsCapacity() {
        Span<Character> src = new Span<>(new Character[] { 'a', 'b', 'c' });
        Span<Character> bigEnough = new Span<>(new Character[4]);
        assertTrue(src.tryCopyTo(bigEnough));
        assertEquals(Character.valueOf('c'), bigEnough.get(2));

        Span<Character> tooSmall = new Span<>(new Character[2]);
        assertFalse(src.tryCopyTo(tooSmall));
    }
}
