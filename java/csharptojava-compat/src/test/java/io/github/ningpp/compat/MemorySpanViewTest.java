package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

/**
 * Memory&lt;T&gt; must expose a writable Span view whose writes reach the backing
 * array (C# Memory&lt;T&gt;.Span semantics), unlike ReadOnlyMemory.
 */
class MemorySpanViewTest {
    @Test
    void writesThroughMemorySpanReachBackingArray() {
        byte[] backing = new byte[] { 1, 2, 3, 4 };
        Memory<Integer> memory = new Memory<>(backing, 1, 2);

        Span<Integer> span = memory.getSpan();
        assertEquals(2, span.getLength());
        span.set(0, 9);
        span.set(1, 10);

        assertEquals(9, backing[1] & 0xFF);
        assertEquals(10, backing[2] & 0xFF);
        assertEquals(1, backing[0] & 0xFF);
        assertEquals(4, backing[3] & 0xFF);
    }

    @Test
    void sliceKeepsWritableView() {
        int[] backing = new int[] { 5, 6, 7 };
        Memory<Integer> memory = new Memory<>(backing);
        memory.slice(1, 2).getSpan().set(0, 60);

        assertEquals(60, backing[1]);
        assertEquals(7, backing[2]);
    }

    @Test
    void clearingMemoryZeroesOnlyItsRange() {
        char[] backing = new char[] { 'a', 'b', 'c' };
        Memory<Character> memory = new Memory<>(backing, 1, 1);
        memory.clear();

        assertEquals('a', backing[0]);
        assertEquals('\0', backing[1]);
        assertEquals('c', backing[2]);
    }
}
