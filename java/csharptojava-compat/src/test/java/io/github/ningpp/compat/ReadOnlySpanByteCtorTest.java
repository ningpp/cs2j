package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

/**
 * ReadOnlySpan must accept a primitive byte[] backing (C# byte[] implicitly
 * converts to ReadOnlySpan&lt;byte&gt;, and the converter wraps the array in
 * new ReadOnlySpan&lt;&gt;(byteArr) with the element type inferred from the target
 * ReadOnlySpan&lt;Integer&gt;). Without the byte[] constructors the diamond
 * inference fails to compile (HexConverter.getCharToHexLookup).
 */
public class ReadOnlySpanByteCtorTest {

    @Test
    public void diamondOverByteArray_infersIntegerElement() {
        ReadOnlySpan<Integer> lookup = new ReadOnlySpan<>(new byte[] { (byte) 0xFF, 0x0, 0xB });
        assertEquals(3, lookup.length());
        assertEquals(Integer.valueOf(0xFF), lookup.get(0));
        assertEquals(Integer.valueOf(0), lookup.get(1));
        assertEquals(Integer.valueOf(0xB), lookup.get(2));
    }

    @Test
    public void sliceOverByteArray_keepsUnsignedValues() {
        ReadOnlySpan<Integer> lookup = new ReadOnlySpan<>(new byte[] { 1, 2, 3, 4 }, 1, 3);
        assertEquals(3, lookup.length());
        assertEquals(Integer.valueOf(2), lookup.get(0));
        assertEquals(Integer.valueOf(4), lookup.get(2));
    }

    @Test
    public void sliceOverByteArray_returnsViewNotCopy() {
        byte[] data = { 10, 20, 30, 40, 50 };
        ReadOnlySpan<Integer> span = new ReadOnlySpan<>(data, 1, 3);
        ReadOnlySpan<Integer> sliced = span.slice(1);
        assertEquals(2, sliced.length());
        assertEquals(Integer.valueOf(30), sliced.get(0));
        assertEquals(Integer.valueOf(40), sliced.get(1));
        assertTrue(sliced.getArray() instanceof byte[],
            "slicing a byte-backed span must stay a view over the original byte[]");
        data[2] = 99;
        assertEquals(Integer.valueOf(99), sliced.get(0));
    }
}
