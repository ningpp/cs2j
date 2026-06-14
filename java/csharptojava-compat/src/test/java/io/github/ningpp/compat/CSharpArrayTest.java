package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

import org.junit.jupiter.api.Test;

class CSharpArrayTest {
    @Test
    void wrapsPrimitiveArrayLengthAndValues() {
        int[] values = {1, 2, 3};
        CSharpArray array = CSharpArray.of(values);

        assertEquals(3, array.getLength());
        assertEquals(2, array.getValue(1));
        assertSame(values, array.unwrap());
    }

    @Test
    void setValueWritesThroughToWrappedArray() {
        String[] values = {"a", "b"};
        CSharpArray array = CSharpArray.of(values);

        array.setValue("c", 1);

        assertEquals("c", values[1]);
        assertEquals("c", array.getValue(1));
    }
}
