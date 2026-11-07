package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

/**
 * C# ArraySegment&lt;byte&gt; backs a primitive byte[] in the compat layer because
 * Java's byte[] is not an Object[]. Reading its Array property through the erased
 * T[] return used to throw ClassCastException (real chain:
 * JsonDocument.clearAndReturn null-checks the rented segment).
 */
class ArraySegmentByteArrayTest {

    @Test
    void getArrayOfByteBackedSegmentReturnsBoxedView() {
        ArraySegment<Integer> segment = new ArraySegment<>(new byte[] {1, 2, 3});

        Object[] view = segment.getArray();

        assertEquals(3, view.length);
        assertEquals((byte) 2, view[1]);
        assertArrayEquals(new byte[] {1, 2, 3}, segment.getByteArray());
    }

    @Test
    void getArrayOfReferenceSegmentIsTheUnderlyingArray() {
        Integer[] data = {1, 2};

        assertSame(data, new ArraySegment<>(data).getArray());
    }
}
