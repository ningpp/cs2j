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

    /**
     * Advances a pointer segment by a byte delta while keeping its bounds when
     * possible. C# pointer arithmetic on a heap segment must stay on the same
     * segment; MemorySegment.ofAddress would turn it into a zero-length native
     * segment and make subsequent reads fail.
     */
    public static MemorySegment advance(MemorySegment segment, long deltaBytes) {
        if (segment == null) {
            return null;
        }
        if (deltaBytes >= 0 && deltaBytes <= segment.byteSize()) {
            return segment.asSlice(deltaBytes);
        }
        return MemorySegment.ofAddress(segment.address() + deltaBytes);
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

    public static short getShort(MemorySegment segment, long offset) {
        int lo = segment.get(ValueLayout.JAVA_BYTE, offset) & 0xFF;
        int hi = segment.get(ValueLayout.JAVA_BYTE, offset + 1) & 0xFF;
        return (short) (ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN ? (lo << 8) | hi : (hi << 8) | lo);
    }

    public static void setShort(MemorySegment segment, long offset, int value) {
        if (ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN) {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) ((value >> 8) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) (value & 0xFF));
        } else {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) (value & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) ((value >> 8) & 0xFF));
        }
    }

    public static int getInt(MemorySegment segment, long offset) {
        int v0 = segment.get(ValueLayout.JAVA_BYTE, offset) & 0xFF;
        int v1 = segment.get(ValueLayout.JAVA_BYTE, offset + 1) & 0xFF;
        int v2 = segment.get(ValueLayout.JAVA_BYTE, offset + 2) & 0xFF;
        int v3 = segment.get(ValueLayout.JAVA_BYTE, offset + 3) & 0xFF;
        if (ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN) {
            return (v0 << 24) | (v1 << 16) | (v2 << 8) | v3;
        }
        return (v3 << 24) | (v2 << 16) | (v1 << 8) | v0;
    }

    public static void setInt(MemorySegment segment, long offset, int value) {
        if (ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN) {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) ((value >> 24) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) ((value >> 16) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 2, (byte) ((value >> 8) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 3, (byte) (value & 0xFF));
        } else {
            segment.set(ValueLayout.JAVA_BYTE, offset, (byte) (value & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 1, (byte) ((value >> 8) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 2, (byte) ((value >> 16) & 0xFF));
            segment.set(ValueLayout.JAVA_BYTE, offset + 3, (byte) ((value >> 24) & 0xFF));
        }
    }

    public static long getLong(MemorySegment segment, long offset) {
        long result = 0;
        for (int i = 0; i < 8; i++) {
            long b = segment.get(ValueLayout.JAVA_BYTE, offset + i) & 0xFFL;
            int shift = ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN ? (7 - i) * 8 : i * 8;
            result |= b << shift;
        }
        return result;
    }

    public static void setLong(MemorySegment segment, long offset, long value) {
        for (int i = 0; i < 8; i++) {
            int shift = ByteOrder.nativeOrder() == ByteOrder.BIG_ENDIAN ? (7 - i) * 8 : i * 8;
            segment.set(ValueLayout.JAVA_BYTE, offset + i, (byte) ((value >> shift) & 0xFF));
        }
    }

    /**
     * Copies bytes written into a temporary byte[] segment back into the original
     * Integer-backed Span. Generated unsafe code materializes Span&lt;Integer&gt;
     * destinations as byte[] for MemorySegment access; without this the writes are
     * lost when the converter's ToUtf8 returns.
     */
    public static void copyBackToSpan(Span<Integer> destination, MemorySegment bytes) {
        copyBackToSpan(destination, bytes, destination == null ? 0 : destination.getLength());
    }

    /**
     * Copies only the bytes actually written back into the original Integer-backed
     * Span. Copying the whole segment would overwrite unwritten positions with
     * zero bytes.
     */
    public static void copyBackToSpan(Span<Integer> destination, MemorySegment bytes, int count) {
        if (destination == null || bytes == null) {
            return;
        }
        count = Math.min(Math.min(destination.getLength(), (int) bytes.byteSize()), count);
        for (int i = 0; i < count; i++) {
            destination.set(i, bytes.get(ValueLayout.JAVA_BYTE, i) & 0xFF);
        }
    }
}
