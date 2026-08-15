package io.github.ningpp.compat;

import java.lang.foreign.MemorySegment;
import java.lang.foreign.ValueLayout;
import java.nio.ByteOrder;

/**
 * Unaligned memory access helpers. C# pointers allow char/int reads at any byte
 * offset; Java's ValueLayout.JAVA_CHAR on a heap byte[] segment requires 2-byte
 * alignment and throws otherwise. These helpers read/write via JAVA_BYTE so the
 * converted unsafe code keeps C# semantics.
 */
public final class MemorySegmentHelper {
    private MemorySegmentHelper() {
    }

    public static int getChar(MemorySegment segment, long offset) {
        int lo = segment.get(ValueLayout.JAVA_BYTE, offset) & 0xFF;
        int hi = segment.get(ValueLayout.JAVA_BYTE, offset + 1) & 0xFF;
        return ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN ? (lo << 8) | hi : (hi << 8) | lo;
    }

    public static void setChar(MemorySegment segment, long offset, int value) {
        if (ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN) {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) ((value >> 8) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) (value & 0xFF));
        } else {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) (value & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) ((value >> 8) & 0xFF));
        }
    }
}
