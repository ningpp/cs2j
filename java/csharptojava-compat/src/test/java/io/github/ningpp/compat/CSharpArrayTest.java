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

    private record Named(String name) {
    }
}
