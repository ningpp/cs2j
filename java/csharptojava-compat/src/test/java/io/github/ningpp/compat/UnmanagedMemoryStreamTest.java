package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import java.lang.foreign.MemorySegment;
import java.lang.foreign.ValueLayout;

import static org.junit.jupiter.api.Assertions.assertEquals;

class UnmanagedMemoryStreamTest {
    @Test
    void positionPointerReturnsSliceAtCurrentPosition() {
        UnmanagedMemoryStream stream = new UnmanagedMemoryStream(new byte[] { 10, 20, 30 });

        stream.setPosition(1);
        MemorySegment pointer = stream.getPositionPointer();

        assertEquals(2, pointer.byteSize());
        assertEquals((byte)20, pointer.get(ValueLayout.JAVA_BYTE, 0));
        assertEquals((byte)30, pointer.get(ValueLayout.JAVA_BYTE, 1));
    }
}
