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
}
