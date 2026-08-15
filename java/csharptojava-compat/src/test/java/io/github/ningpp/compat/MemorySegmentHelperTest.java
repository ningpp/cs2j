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
}
