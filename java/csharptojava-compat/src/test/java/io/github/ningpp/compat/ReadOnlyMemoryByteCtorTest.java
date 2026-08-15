package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;

/**
 * ReadOnlyMemory must accept a primitive byte[] backing and materialize
 * primitive byte[] results, mirroring the ReadOnlySpan byte[] support.
 * The converter emits these calls for C# ReadOnlyMemory&lt;byte&gt;:
 *   - new ReadOnlyMemory&lt;&gt;(byteArr)  (implicit byte[] → ReadOnlyMemory&lt;byte&gt;)
 *   - memory.toByteArray()              (ReadOnlyMemory&lt;byte&gt;.ToArray())
 */
public class ReadOnlyMemoryByteCtorTest {

    @Test
    public void diamondOverByteArray_holdsUnsignedValues() {
        ReadOnlyMemory<Integer> memory = new ReadOnlyMemory<>(new byte[] { (byte) 0xFF, 0x0, 0xB });
        assertEquals(3, memory.length());
        assertEquals(Integer.valueOf(0xFF), memory.getSpan().get(0));
        assertEquals(Integer.valueOf(0), memory.getSpan().get(1));
        assertEquals(Integer.valueOf(0xB), memory.getSpan().get(2));
    }

    @Test
    public void toByteArray_returnsPrimitiveBytes() {
        ReadOnlyMemory<Integer> memory = new ReadOnlyMemory<>(new Integer[] { 1, 2, 0xFF });
        assertArrayEquals(new byte[] { 1, 2, (byte) 0xFF }, memory.toByteArray());
    }

    @Test
    public void toByteArray_overByteArrayBacking_roundTrips() {
        ReadOnlyMemory<Integer> memory = new ReadOnlyMemory<>(new byte[] { (byte) 0xFF, 0x40, 0x00 });
        assertArrayEquals(new byte[] { (byte) 0xFF, 0x40, 0x00 }, memory.toByteArray());
    }

    @Test
    public void byteSliceMemory_isViewNotCopy() {
        byte[] data = new byte[] { 1, 2, 3, 4, 5 };
        ReadOnlyMemory<Integer> memory = MemoryExtensions.asMemory(data, 1, 3);
        assertEquals(3, memory.length());
        // The span returned by getSpan() must write back into the original array
        // (StreamWrapper.readAsync writes through it), not into a discarded copy.
        memory.getSpan().set(0, 9);
        memory.getSpan().set(2, 7);
        assertEquals(9, data[1]);
        assertEquals(7, data[3]);
        assertEquals(1, data[0]);
        assertEquals(3, data[2]);
        assertEquals(5, data[4]);
    }

    @Test
    public void byteSliceConstructor_isViewNotCopy() {
        byte[] data = new byte[] { 10, 20, 30, 40 };
        ReadOnlyMemory<Integer> memory = new ReadOnlyMemory<>(data, 1, 2);
        memory.getSpan().set(0, 99);
        assertEquals(99, data[1]);
        assertEquals(30, data[2]);
    }

    @Test
    public void toByteArray_overObjectBacking_worksForBoxedIntegers() {
        // ArrayBufferWriter<Integer> keeps an Object[] buffer; copies of it must
        // still materialize as primitive bytes.
        @SuppressWarnings("unchecked")
        ReadOnlyMemory<Integer> memory = (ReadOnlyMemory<Integer>) (ReadOnlyMemory<?>) new ReadOnlyMemory<>(new Object[] { 1, 2, 0xFF });
        assertArrayEquals(new byte[] { 1, 2, (byte) 0xFF }, memory.toByteArray());
    }
}
