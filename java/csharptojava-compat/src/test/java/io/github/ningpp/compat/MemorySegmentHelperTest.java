package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;

import java.lang.foreign.MemorySegment;

import org.junit.jupiter.api.Test;

class MemorySegmentHelperTest {
    @Test
    void unalignedCharReadWriteRoundTrips() {
        MemorySegment segment = MemorySegment.ofArray(new byte[4]);
        MemorySegmentHelper.setChar(segment, 1, 0x4A42);
        assertEquals(0x4A42, MemorySegmentHelper.getChar(segment, 1));
    }

    @Test
    void advanceKeepsHeapSegmentBounds() {
        MemorySegment segment = MemorySegment.ofArray(new byte[8]);
        MemorySegment advanced = MemorySegmentHelper.advance(segment, 2);
        assertEquals(6, advanced.byteSize());
        advanced.set(java.lang.foreign.ValueLayout.JAVA_BYTE, 0, (byte) 42);
        assertEquals(42, segment.get(java.lang.foreign.ValueLayout.JAVA_BYTE, 2));
    }

    @Test
    void unalignedIntReadWriteRoundTrips() {
        MemorySegment segment = MemorySegment.ofArray(new byte[8]);
        MemorySegmentHelper.setInt(segment, 1, 0x12345678);
        assertEquals(0x12345678, MemorySegmentHelper.getInt(segment, 1));
    }
}
