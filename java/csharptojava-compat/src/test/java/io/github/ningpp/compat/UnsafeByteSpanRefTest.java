package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * Unsafe.AddByteOffset(ref byte, IntPtr) must read the byte at the requested
 * offset of the span that MemoryMarshal.GetReference was called on. Regression:
 * the holder-based ref lowering returned only the first byte, so
 * JsonReaderHelper.indexOfQuoteOrAnyControlOrBackSlash never saw past byte 0
 * and threw "Expected end of string" for every non-empty JSON string.
 */
public class UnsafeByteSpanRefTest {

    @Test
    public void addByteOffset_readsOffsetFromSpanRef() {
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>(new byte[] { 'a', 'b', '"', 'c' });
        IntHolder ref = Unsafe.createByteSpanRef(span);

        assertEquals('a', Unsafe.addByteOffset(ref, 0));
        assertEquals('b', Unsafe.addByteOffset(ref, 1));
        assertEquals('"', Unsafe.addByteOffset(ref, 2));
        assertEquals('c', Unsafe.addByteOffset(ref, 3));
    }

    @Test
    public void addByteOffset_stillPassesThroughPlainHolders() {
        IntHolder plain = new IntHolder(42);
        assertEquals(42, Unsafe.addByteOffset(plain, 7));
    }
}
