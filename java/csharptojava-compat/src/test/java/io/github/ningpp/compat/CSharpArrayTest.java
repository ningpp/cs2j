package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import java.util.Comparator;

import org.junit.jupiter.api.Test;

class CSharpArrayTest {
    @Test
    void wrapsPrimitiveArrayLengthAndValues() {
        int[] values = {1, 2, 3};
        CSharpArray array = CSharpArray.of(values);

        assertEquals(3, array.getLength());
        assertEquals(2, array.getValue(1));
        assertSame(values, array.unwrap());
        assertSame(values, array.as(int[].class));
    }

    @Test
    void setValueWritesThroughToWrappedArray() {
        String[] values = {"a", "b"};
        CSharpArray array = CSharpArray.of(values);

        array.setValue("c", 1);

        assertEquals("c", values[1]);
        assertEquals("c", array.getValue(1));
    }

    @Test
    void binarySearchUsesObjectComparatorForReferenceArrays() {
        Named[] values = {
            new Named("a"),
            new Named("b"),
            new Named("c")
        };
        Comparator<Object> comparer = Comparator.comparing(o -> ((Named) o).name);

        assertEquals(1, CSharpArray.binarySearch(values, new Named("b"), comparer));
        assertEquals(~1, CSharpArray.binarySearch(values, new Named("aa"), comparer));
    }

    /**
     * The erased T[] conversions re-materialize a primitive array from a boxed
     * Object[]/wrapper[] source. java.lang.reflect.Array.set only performs widening
     * conversions, so an Integer element (or a null from a pool/rented Object[])
     * used to throw IllegalArgumentException; the C# unboxing conversion must
     * narrow and map null to the primitive default instead.
     */
    @Test
    void toTypedArrayNarrowsBoxedNumbersIntoPrimitiveArrays() {
        byte[] bytes = (byte[]) CSharpArray.toTypedArray(new Object[] {null, 1, 256, -1}, byte.class);
        assertEquals(0, bytes[0]);
        assertEquals(1, bytes[1]);
        assertEquals(0, bytes[2]);
        assertEquals(-1, bytes[3]);

        long[] longs = (long[]) CSharpArray.toTypedArray(new Object[] {null, 7, 8L}, long.class);
        assertEquals(0L, longs[0]);
        assertEquals(7L, longs[1]);
        assertEquals(8L, longs[2]);

        char[] chars = (char[]) CSharpArray.toTypedArray(new Object[] {null, 'A', 66}, char.class);
        assertEquals('\0', chars[0]);
        assertEquals('A', chars[1]);
        assertEquals('B', chars[2]);

        boolean[] booleans = (boolean[]) CSharpArray.toTypedArray(new Object[] {null, true, false}, boolean.class);
        assertEquals(false, booleans[0]);
        assertEquals(true, booleans[1]);
        assertEquals(false, booleans[2]);
    }

    private record Named(String name) {
    }
}
