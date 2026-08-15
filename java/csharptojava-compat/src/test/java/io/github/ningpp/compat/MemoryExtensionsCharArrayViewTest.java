package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * Regression tests for char[].AsSpan() semantics: the span must be a view
 * over the original char[] so Fill/CopyTo write through (C# Span<char>
 * mutates the backing array).
 */
class MemoryExtensionsCharArrayViewTest {

    @Test
    void asSpanCharArrayFillWritesBackToOriginalArray() {
        char[] data = new char[5];
        MemoryExtensions.asSpan(data).fill('a');
        assertArrayEquals(new char[] {'a', 'a', 'a', 'a', 'a'}, data);
    }

    @Test
    void asSpanCharArraySliceFillWritesBackAtCorrectOffset() {
        char[] data = new char[6];
        MemoryExtensions.asSpan(data, 2, 3).fill('x');
        assertArrayEquals(new char[] {'\0', '\0', 'x', 'x', 'x', '\0'}, data);
    }

    @Test
    void readOnlySpanCopyToCharSpanWritesThroughToCharArray() {
        char[] target = new char[8];
        ReadOnlySpan<Character> escaped = MemoryExtensions.asSpan(new char[] {'\\', 'u', '0', '0', '6', '1'});
        escaped.copyTo(new Span<Character>(MemoryExtensions.asSpan(target, 1)));
        assertArrayEquals(new char[] {'\0', '\\', 'u', '0', '0', '6', '1', '\0'}, target);
    }

    @Test
    void asSpanCharArrayGetReadsOriginalElements() {
        char[] data = {'a', 'b', 'c'};
        ReadOnlySpan<Character> span = MemoryExtensions.asSpan(data);
        assertEquals('b', span.get(1));
    }
}
