package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;
import static org.junit.jupiter.api.Assertions.*;

class SpanTest {

    @Test
    void fullArrayConstructor() {
        String[] arr = {"a", "b", "c"};
        Span<String> span = new Span<>(arr);
        assertEquals(3, span.length());
        assertEquals("a", span.get(0));
        assertEquals("c", span.get(2));
    }

    @Test
    void sliceConstructor() {
        String[] arr = {"a", "b", "c", "d", "e"};
        Span<String> span = new Span<>(arr, 1, 3);
        assertEquals(3, span.length());
        assertEquals("b", span.get(0));
        assertEquals("d", span.get(2));
    }

    @Test
    void setAndGet() {
        Character[] arr = new Character[5];
        Span<Character> span = new Span<>(arr);
        span.set(2, 'X');
        assertEquals('X', span.get(2));
        assertEquals('X', arr[2]);
    }

    @Test
    void integerSpanCanWrapPrimitiveByteArray() {
        byte[] arr = new byte[] {0, 0, 0};
        Span<Integer> span = new Span<>(arr);
        span.set(1, 255);
        assertEquals(255, span.get(1));
        assertEquals((byte)255, arr[1]);
    }

    @Test
    void readOnlyIntegerSpanCopiesToPrimitiveByteSpan() {
        ReadOnlySpan<Integer> source = new ReadOnlySpan<>(new Integer[] {0xEF, 0xBB, 0xBF});
        byte[] target = new byte[] {0, 0, 0, 0};
        source.copyTo(new Span<Integer>(target).slice(1));

        assertArrayEquals(new byte[] {0, (byte)0xEF, (byte)0xBB, (byte)0xBF}, target);
    }

    @Test
    void sliceSharesUnderlyingArray() {
        Character[] arr = {'a', 'b', 'c', 'd'};
        Span<Character> span = new Span<>(arr);
        Span<Character> sliced = span.slice(1, 2);
        assertEquals(2, sliced.length());
        assertEquals('b', sliced.get(0));
        sliced.set(0, 'Z');
        assertEquals('Z', arr[1]);
    }

    @Test
    void sliceFromStart() {
        Integer[] arr = {1, 2, 3, 4};
        Span<Integer> span = new Span<>(arr);
        Span<Integer> sliced = span.slice(2);
        assertEquals(2, sliced.length());
        assertEquals(3, sliced.get(0));
        assertEquals(4, sliced.get(1));
    }

    @Test
    void toArrayReturnsCopy() {
        String[] arr = {"x", "y"};
        Span<String> span = new Span<>(arr);
        String[] copy = span.toArray();
        assertArrayEquals(arr, copy);
        copy[0] = "modified";
        assertEquals("x", span.get(0));
    }
}
