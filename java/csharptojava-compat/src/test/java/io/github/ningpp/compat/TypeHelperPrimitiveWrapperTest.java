package io.github.ningpp.compat;

import org.junit.jupiter.api.Test;

import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;

class TypeHelperPrimitiveWrapperTest {

    @Test
    void isSameType_primitiveAndWrapperAreSame() {
        assertTrue(TypeHelper.isSameType(boolean.class, Boolean.class));
        assertTrue(TypeHelper.isSameType(int.class, Integer.class));
        assertTrue(TypeHelper.isSameType(long.class, Long.class));
        assertTrue(TypeHelper.isSameType(short.class, Short.class));
        assertTrue(TypeHelper.isSameType(byte.class, Byte.class));
        assertTrue(TypeHelper.isSameType(char.class, Character.class));
        assertTrue(TypeHelper.isSameType(float.class, Float.class));
        assertTrue(TypeHelper.isSameType(double.class, Double.class));
        assertFalse(TypeHelper.isSameType(int.class, Long.class));
        assertFalse(TypeHelper.isSameType(int.class, null));
    }

    @Test
    void isAssignableFrom_primitiveAndWrapperAreAssignable() {
        assertTrue(TypeHelper.isAssignableFrom(Boolean.class, boolean.class));
        assertTrue(TypeHelper.isAssignableFrom(boolean.class, Boolean.class));
        assertTrue(TypeHelper.isAssignableFrom(Integer.class, int.class));
        assertTrue(TypeHelper.isAssignableFrom(int.class, Integer.class));
        assertFalse(TypeHelper.isAssignableFrom(Long.class, int.class));
    }
}
