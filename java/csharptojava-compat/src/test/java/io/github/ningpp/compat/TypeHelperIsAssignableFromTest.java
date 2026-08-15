package io.github.ningpp.compat;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.assertEquals;

import org.junit.jupiter.api.Test;

class TypeHelperIsAssignableFromTest {
    enum E {
        A
    }

    @Test
    void arraysAreAssignableToIEnumerableCompat() {
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, String[].class));
        assertTrue(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, int[].class));
        assertFalse(TypeHelper.isAssignableFrom(CSharpGenericIterable.class, String.class));
    }

    @Test
    void enumTypeCodeIsInt32() {
        assertEquals(TypeCode.Int32, TypeHelper.getTypeCode(E.class));
    }

    @Test
    void enumValuesReturnConstants() {
        CSharpArray values = EnumHelper.getValues(E.class);
        assertEquals(1, values.getLength());
        assertSame(E.A, values.getValue(0));
    }
}
